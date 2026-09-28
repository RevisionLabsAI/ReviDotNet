using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Decisions;

/// <summary>Selection, source preservation, failure fallback, isolation, and adaptive budget checks.</summary>
public sealed class DecisionIntegrationTests
{
    [Theory]
    [InlineData(true, 1, 2, "sufficient-evidence")]
    [InlineData(false, 2, 5, "round-limit")]
    public async Task AdaptiveSearchStopsOnlyWithMatchingPoliciesAndRemainsBounded(bool sufficient, int rounds, int calls, string reason)
    {
        string hash = new('a', 64);
        FakeDecisions decisions = new((prompt, _, _) => Task.FromResult(prompt == "research-stop"
            ? new DecisionRun("fixed-version", new Dictionary<string, DecisionAnswer>
            {
                ["sufficient"] = new BooleanAnswer(sufficient ? .99 : .5),
                ["unresolved"] = new BooleanAnswer(.01), ["more-useful"] = new BooleanAnswer(.01)
            }, new(1, 0)) { PromptHash = hash }
            : PassageRun(.99) with { PromptHash = hash }));
        DocumentCollection collection = new("scope", Enumerable.Range(0, 10).Select(i => new SearchDocument(i.ToString(), "Refund rules.", "file-" + i)));
        AdaptiveDocumentSearch adaptive = new(new DocumentSearchService(decisions, Embeddings()), decisions);
        AdaptiveSearchResult result = await adaptive.SearchAsync(collection, "refund", new AdaptiveSearchOptions
        {
            MaximumRounds = 2, MaximumDecisionCalls = 5,
            Search = new() { CandidateCount = 1, ResultCount = 1, EvidencePolicy = Policy("evidence") },
            Sufficiency = Policy("sufficient"), Contradictions = Policy("unresolved"), SearchUtility = Policy("more-useful")
        });
        result.StopReason.Should().Be(reason); result.Rounds.Should().Be(rounds);
        result.ReservedDecisionCalls.Should().Be(calls); decisions.Calls.Should().Be(calls);
    }

    private static DecisionRegistry Registry()
    {
        DecisionRegistry registry = new(); registry.LoadAssembly(typeof(DecisionRegistry).Assembly);
        registry.Add(new DecisionModelProfile { Name = "decision-default", ModelString = "fixed-version", ProviderName = "test", RevisionPinned = true });
        return registry;
    }
    private static IEmbedService Embeddings()
    {
        ProviderManagerService providers = new(new RecordingReviLogger<ProviderManagerService>());
        return new EmbedService(new EmbeddingManagerService(providers, new RecordingReviLogger<EmbeddingManagerService>()), new RecordingReviLogger<EmbedService>());
    }

    [Fact]
    public async Task SelectorRetainsRequiredToolsAndFallsBackOnIncompleteAnswers()
    {
        FakeDecisions decisions = new((_, _, _) => Task.FromResult(new DecisionRun("fixed-version", new Dictionary<string, DecisionAnswer>
        {
            ["rank"] = new ChoiceAnswer("search", .8, new Dictionary<string, double> { ["search"] = .8, ["read"] = .1, ["write"] = .1 }),
            ["any-fit"] = new BooleanAnswer(.9)
        }, new(1, 0))));
        DecisionContextSelector selector = new(decisions, Registry());
        ContextCandidate[] authorized = [new("search", "Search"), new("read", "Read", Required: true), new("write", "Write")];
        ContextSelectionResult result = await selector.SelectAsync("request", authorized, new() { MaximumSelected = 1 });
        result.Candidates.Select(c => c.Id).Should().BeEquivalentTo("search", "read");
        decisions.Handler = (_, _, _) => Task.FromResult(new DecisionRun("fixed-version", new Dictionary<string, DecisionAnswer>
        {
            ["rank"] = new ChoiceAnswer("search", 1, new Dictionary<string, double> { ["search"] = 1 }), ["any-fit"] = new BooleanAnswer(.9)
        }, new(1, 0)));
        result = await selector.SelectAsync("request", authorized, new());
        result.Candidates.Should().HaveCount(3); result.FallbackReason.Should().Be("incomplete-catalog-answer");
    }

    [Fact]
    public async Task SelectorDoesNotTruncateOversizeCatalogOrHideProviderFailure()
    {
        FakeDecisions decisions = new((_, _, _) => throw new IOException("provider failed"));
        DecisionContextSelector selector = new(decisions, Registry());
        ContextCandidate[] tooMany = Enumerable.Range(0, 256).Select(i => new ContextCandidate(i.ToString(), "tool")).ToArray();
        (await selector.SelectAsync("q", tooMany, new())).Candidates.Should().HaveCount(256);
        decisions.Calls.Should().Be(0);
        ContextSelectionResult result = await selector.SelectAsync("q", [new("one", "tool")], new());
        result.FallbackReason.Should().Be("selector-unavailable"); result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task SourceSpansRemainExactAndCollectionsStaySeparate()
    {
        string source = "Heading\r\nRefunds are available within thirty days.\r\n" + new string('x', 300);
        DocumentCollection first = new("first", [new("doc-a", source, "a.txt")], 128);
        DocumentCollection second = new("second", [new("doc-b", "No cancellation terms in this file.", "b.txt")]);
        foreach (DocumentPassage passage in first.Passages) source.Substring(passage.Start, passage.Length).Should().Be(passage.Text);
        FakeDecisions decisions = new((_, _, _) => throw new IOException());
        DocumentSearchService search = new(decisions, Embeddings());
        DocumentSearchResult result = await search.SearchAsync(first, "refunds", new() { DecisionPrompt = null });
        result.Hits.Should().ContainSingle(); result.Hits[0].Passage.DocumentId.Should().Be("doc-a");
        result.Hits[0].Passage.SourceVersion.Should().HaveLength(64);
        (await search.SearchAsync(second, "refunds", new() { DecisionPrompt = null })).Status.Should().Be(DocumentSearchStatus.NotFound);
        decisions.Calls.Should().Be(0);
    }

    [Fact]
    public async Task PartialRerankKeepsFailedCandidatesAndSignalsUncertainty()
    {
        DocumentCollection collection = new("scope", [new("a", "Refund refunds refunds.", "a"), new("b", "Refund rules.", "b")]);
        FakeDecisions decisions = new((_, state, _) => state.ToString()!.Contains("rules") ? Task.FromResult(PassageRun(.99)) : throw new IOException());
        DocumentSearchService search = new(decisions, Embeddings());
        DocumentSearchResult baseline = await search.SearchAsync(collection, "refund", new() { DecisionPrompt = null });
        DocumentSearchResult judged = await search.SearchAsync(collection, "refund");
        judged.Hits.Select(h => h.Passage.Id).Should().Equal(baseline.Hits.Select(h => h.Passage.Id));
        judged.Status.Should().Be(DocumentSearchStatus.NeedsMoreSearch);
        judged.FallbackReason.Should().Be("decision-incomplete-or-unavailable");
    }

    [Fact]
    public async Task CitationPresenceIsCheckedWithoutCallingModel()
    {
        FakeDecisions decisions = new((_, _, _) => throw new Exception());
        CitationVerifier verifier = new(decisions);
        DocumentPassage passage = new("p", "d", "v", "file", 0, 10, 1, "Real quote");
        CitationCheck result = await verifier.CheckAsync(passage, "invented", "claim", Policy("relation"));
        result.QuotePresent.Should().BeFalse(); decisions.Calls.Should().Be(0);
    }

    [Fact]
    public void ExtractorRejectsPdfAndBinaryTextInsteadOfDecodingThem()
    {
        DocumentTextExtractor extractor = new();
        extractor.Extract(new SessionFile { Id = "pdf", Name = "a.pdf", MediaType = "application/pdf", Bytes = Encoding.ASCII.GetBytes("%PDF-1.7") }).Should().BeNull();
        Action binary = () => extractor.Extract(new SessionFile { Id = "txt", Name = "a.txt", MediaType = "text/plain", Bytes = [0, 1] });
        binary.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task AdaptiveSearchCannotExceedCallBudget()
    {
        FakeDecisions decisions = new((_, _, _) => throw new Exception());
        AdaptiveDocumentSearch adaptive = new(new DocumentSearchService(decisions, Embeddings()), decisions);
        AdaptiveSearchResult result = await adaptive.SearchAsync(new("scope", [new("d", "refund", "file")]), "refund", new AdaptiveSearchOptions
        {
            MaximumDecisionCalls = 1, Search = new() { CandidateCount = 20 },
            Sufficiency = Policy("sufficient"), Contradictions = Policy("unresolved"), SearchUtility = Policy("more-useful")
        });
        result.StopReason.Should().Be("call-limit"); result.Rounds.Should().Be(0); decisions.Calls.Should().Be(0);
    }

    private static DecisionPolicy Policy(string question) => new() { Name = question, Model = "fixed-version", PromptHash = new string('a', 64), Question = question, TrafficSlice = "test", AcceptAt = .9, RejectAt = .1 };
    private static DecisionRun PassageRun(double evidence) => new("fixed-version", new Dictionary<string, DecisionAnswer>
    {
        ["relevant"] = new BooleanAnswer(.9), ["evidence"] = new BooleanAnswer(evidence), ["contradicts"] = new BooleanAnswer(.1), ["instructions"] = new BooleanAnswer(.1)
    }, new(1, 0));

    internal sealed class FakeDecisions(Func<string, object, CancellationToken, Task<DecisionRun>> handler) : IDecisionService
    {
        public Func<string, object, CancellationToken, Task<DecisionRun>> Handler { get; set; } = handler;
        public int Calls;
        public Task<DecisionRun> EvaluateAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null) { Interlocked.Increment(ref Calls); return Handler(promptName, state, token); }
        public Task<ChoiceAnswer<T>> ChoiceAsync<T>(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null) where T : struct, Enum => throw new NotSupportedException();
        public Task<BooleanAnswer> BooleanAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null) => throw new NotSupportedException();
        public Task<ScoreAnswer> ScoreAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null) => throw new NotSupportedException();
    }
}
