// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// Loop-level tests for <see cref="RefinementController.RunCampaignAsync"/>.
/// <para>
/// Faking the full live agent-run pipeline (real LLM + tools) is out of scope for a unit test, so these
/// tests stub the run pipeline (<see cref="FakeAgentService"/> + <see cref="FakeJudge"/>) and the proposer,
/// and exercise the loop's control flow: baseline measurement, the no-improvement stop criterion, the
/// accept path through the shared <see cref="GatePolicy"/>, and budget exhaustion. The pure gate function
/// itself is unit-tested exhaustively in <see cref="GatePolicyTests"/>; a real end-to-end run with the live
/// judge/pairwise LLMs and real candidate graph validation is covered by the e2e harness, not here.
/// </para>
/// <para>
/// <b>How quality is injected:</b> the fake agent service writes the desired judge quality as the run's
/// final-output string and emits one <c>llm-response</c> trace event carrying token counts; the fake judge
/// reads that number back as the overall score. So the registered agent ("baseline") and the temp-slot
/// candidate can be made to differ in quality deterministically.
/// </para>
/// </summary>
public class RunCampaignTests
{
    private const string AgentDefinition =
        "[[information]]\nname = chatbot\n\n[[_system]]\nYou answer grounded questions.\n";

    private const string RevisedDefinition =
        "[[information]]\nname = chatbot\n\n[[_system]]\nYou answer grounded questions, citing sources.\n";

    /// <summary>
    /// A definition carrying a <c>temperature</c> knob so the deterministic <see cref="SamplingMutator"/> can
    /// fire alongside the LLM proposer — used to exercise the per-round beam (multiple candidates).
    /// </summary>
    private const string TunableDefinition =
        "[[information]]\nname = chatbot\ntemperature = 0.7\n\n[[_system]]\nYou answer grounded questions.\n";

    private static ScenarioSuite Suite() => new()
    {
        Name = "chatbot-core",
        AgentName = "chatbot",
        Scenarios =
        [
            new Scenario { Id = "t1", AgentName = "chatbot", Inputs = new Dictionary<string, string> { ["q"] = "hello" }, Rubric = ["Groundedness"], HeldOut = false },
            new Scenario { Id = "h1", AgentName = "chatbot", Inputs = new Dictionary<string, string> { ["q"] = "world" }, Rubric = ["Groundedness"], HeldOut = true }
        ]
    };

    private static CampaignSpec Spec(long? budget = null, int maxRounds = 10, int stopAfter = 2, long? metaBudget = null) => new()
    {
        PluginName = "gd",
        AgentName = "chatbot",
        SuiteName = "chatbot-core",
        SamplesPerScenario = 1,
        Mode = "live",
        TokenBudget = budget,
        MetaTokenBudget = metaBudget,
        MaxRounds = maxRounds,
        StopAfterNoImprovementRounds = stopAfter
    };

    private static RefinementController Controller(
        IProposalStrategy proposer, IAgentService agentService, RefineryCaptureBroker broker, IInferService pairwiseInfer,
        MetaLlmUsageBroker? meta = null, ILlmJudge? judge = null)
    {
        meta ??= new MetaLlmUsageBroker();
        RefinementRunner runner = new(agentService, broker);
        return new RefinementController(
            runner,
            judge ?? new FakeJudge(),
            new InMemoryCampaignStore(),
            proposer,
            new PairwiseGate(pairwiseInfer, meta),
            new CandidateValidator(),
            new FakeAgentManager(),
            meta);
    }

    [Fact]
    public async Task RunCampaign_NoProposal_ConvergesAfterNoImprovementRounds_WithBaselineAndNoAcceptedVariants()
    {
        RefineryCaptureBroker broker = new();
        FakeAgentService agentService = new(broker, quality: 5);
        RefinementController controller = Controller(new NullProposer(), agentService, broker, new StubPairwiseInfer());

        Campaign result = await controller.RunCampaignAsync(Spec(stopAfter: 2), Suite(), AgentDefinition, checkers: []);

        // Converged via the no-improvement stop criterion.
        result.Status.Should().Be(CampaignStatus.Converged);

        // Baseline was measured and set; current mirrors it (no accepted change).
        result.Baseline.Should().NotBeNull();
        result.Current.Should().NotBeNull();
        result.Baseline!.QualityP10.Should().Be(5);

        // No accepted variants — every recorded iteration has a null AcceptedVariantId.
        result.Iterations.Should().OnlyContain(it => it.AcceptedVariantId == null);
    }

    [Fact]
    public async Task RunCampaign_AcceptsImprovingProposal_AdvancesCurrentAndRecordsAcceptedVariant()
    {
        // Baseline runs at quality 5; the temp-slot candidate runs at quality 8, so the gate's train-p10
        // improvement + pairwise-positive + non-regression all hold → ACCEPT on round 1. Propose once, then
        // null forever so the loop converges right after the single accept.
        RefineryCaptureBroker broker = new();
        FakeAgentService agentService = new(broker, quality: 5, candidateQuality: 8);
        OneShotProposer proposer = new(new Proposal("system-prompt", RevisedDefinition, "+ citing sources", "Add a citation instruction."));

        RefinementController controller = Controller(proposer, agentService, broker, new StubPairwiseInfer());

        Campaign result = await controller.RunCampaignAsync(Spec(stopAfter: 2), Suite(), AgentDefinition, checkers: []);

        result.Status.Should().Be(CampaignStatus.Converged);
        result.Iterations.Should().NotBeEmpty();

        CampaignIteration first = result.Iterations[0];
        first.Round.Should().Be(1);
        first.Variants.Should().NotBeEmpty();

        // The accepted-best variant is recorded and pointed at by AcceptedVariantId.
        first.AcceptedVariantId.Should().NotBeNull();
        VariantRecord accepted = first.Variants.Single(v => v.Id == first.AcceptedVariantId);
        accepted.Accepted.Should().BeTrue();

        // Current advanced to the candidate's (higher) train scores.
        result.Current!.QualityP10.Should().BeGreaterThan(result.Baseline!.QualityP10);
    }

    [Fact]
    public async Task RunCampaign_Beam_RecordsMultipleCandidates_AndAcceptsBestPassing()
    {
        // The tunable definition (temperature = 0.7) lets the deterministic SamplingMutator fire alongside the
        // LLM proposer, so round 1's beam has >1 candidate. Every candidate is scored as the temp slot, so the
        // fake reports candidateQuality 8 (> baseline 5) for all → all pass the gate; the best is accepted.
        RefineryCaptureBroker broker = new();
        FakeAgentService agentService = new(broker, quality: 5, candidateQuality: 8);
        OneShotProposer proposer = new(new Proposal("system-prompt", RevisedDefinition, "+ citing sources", "Add a citation instruction."));
        RefinementController controller = Controller(proposer, agentService, broker, new StubPairwiseInfer());

        Campaign result = await controller.RunCampaignAsync(Spec(stopAfter: 2), Suite(), TunableDefinition, checkers: []);

        result.Status.Should().Be(CampaignStatus.Converged);

        CampaignIteration first = result.Iterations[0];
        first.Round.Should().Be(1);

        // Beam recorded BOTH the LLM proposal and the SamplingMutator candidate (and possibly more).
        first.Variants.Count.Should().BeGreaterThan(1);
        first.Variants.Select(v => v.KnobType).Should().Contain("system-prompt");
        first.Variants.Select(v => v.KnobType).Should().Contain("sampling");

        // The accepted-best is exactly one of the recorded variants and is marked accepted.
        first.AcceptedVariantId.Should().NotBeNull();
        VariantRecord best = first.Variants.Single(v => v.Id == first.AcceptedVariantId);
        best.Accepted.Should().BeTrue();
        best.TrainScores!.QualityP10.Should().Be(8);

        // Current advanced past baseline.
        result.Current!.QualityP10.Should().BeGreaterThan(result.Baseline!.QualityP10);
    }

    [Fact]
    public async Task RunCampaign_MetaBudgetExhausted_StopsWithBudgetExhaustedStatus()
    {
        // Agent runs are free (0 agent tokens), so only the META budget can stop the campaign. The metered
        // judge charges 100 meta tokens per call into the shared broker; a 10-token meta budget is blown by
        // the baseline judging alone, so round 1 stops at the top-of-round meta-budget gate.
        MetaLlmUsageBroker meta = new();
        RefineryCaptureBroker broker = new();
        FakeAgentService agentService = new(broker, quality: 5, candidateQuality: 8);
        OneShotProposer proposer = new(new Proposal("system-prompt", RevisedDefinition, "+ tweak", "tweak"), once: false);
        MeteredJudge judge = new(meta, quality: 5, metaTokensPerCall: 100);
        RefinementController controller = Controller(proposer, agentService, broker, new StubPairwiseInfer(), meta, judge);

        Campaign result = await controller.RunCampaignAsync(
            Spec(budget: null, stopAfter: 5, metaBudget: 10), Suite(), AgentDefinition, checkers: []);

        result.Status.Should().Be(CampaignStatus.BudgetExhausted);
    }

    [Fact]
    public async Task RunCampaign_BudgetExhausted_StopsWithBudgetExhaustedStatus()
    {
        // Each run charges 200 agent-execution tokens; a 10-token budget is blown by the baseline alone.
        RefineryCaptureBroker broker = new();
        FakeAgentService agentService = new(broker, quality: 5, inputTokens: 100, outputTokens: 100);
        // Always propose, so the loop would keep running if not for the budget gate.
        OneShotProposer proposer = new(new Proposal("system-prompt", RevisedDefinition, "+ tweak", "tweak"), once: false);
        RefinementController controller = Controller(proposer, agentService, broker, new StubPairwiseInfer());

        Campaign result = await controller.RunCampaignAsync(Spec(budget: 10, stopAfter: 5), Suite(), AgentDefinition, checkers: []);

        result.Status.Should().Be(CampaignStatus.BudgetExhausted);
        result.TokensSpent.Should().BeGreaterThanOrEqualTo(10);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An <see cref="IProposalStrategy"/> that never proposes a change.</summary>
    private sealed class NullProposer : IProposalStrategy
    {
        public Task<Proposal?> ProposeAsync(string agentName, string currentDefinition, SuiteAggregate scores,
            IReadOnlyList<ScoreCard> cards, CancellationToken ct = default) => Task.FromResult<Proposal?>(null);
    }

    /// <summary>Proposes the given proposal (once by default; or every round when <c>once</c> is false).</summary>
    private sealed class OneShotProposer(Proposal proposal, bool once = true) : IProposalStrategy
    {
        private int _calls;
        public Task<Proposal?> ProposeAsync(string agentName, string currentDefinition, SuiteAggregate scores,
            IReadOnlyList<ScoreCard> cards, CancellationToken ct = default) =>
            Task.FromResult(!once || _calls++ == 0 ? proposal : null);
    }

    /// <summary>A judge that reads the run's final-output string as the overall quality (no LLM).</summary>
    private sealed class FakeJudge : ILlmJudge
    {
        public Task<QualityScore?> JudgeAsync(AgentTrace trace, string agentDefinition, Scenario scenario,
            IReadOnlyList<InvariantResult> invariants, CancellationToken ct = default)
        {
            int overall = int.TryParse(trace.FinalOutput, out int q) ? q : 0;
            return Task.FromResult<QualityScore?>(new QualityScore
            {
                Overall = overall,
                Rationale = "fake",
                Facets = [new FacetScore("Groundedness", overall, "fake")]
            });
        }
    }

    /// <summary>
    /// A judge that reads the run's final-output string as the overall quality AND charges a fixed number of
    /// meta tokens per call into the shared <see cref="MetaLlmUsageBroker"/> (so the meta budget can be
    /// exercised without a live LLM). Mirrors how <see cref="LlmJudge"/> records its <c>ToObjectWithUsage</c>
    /// token usage.
    /// </summary>
    private sealed class MeteredJudge(MetaLlmUsageBroker meta, int quality, int metaTokensPerCall) : ILlmJudge
    {
        public Task<QualityScore?> JudgeAsync(AgentTrace trace, string agentDefinition, Scenario scenario,
            IReadOnlyList<InvariantResult> invariants, CancellationToken ct = default)
        {
            meta.Record(new CompletionResult { InputTokens = metaTokensPerCall, OutputTokens = 0 });
            int overall = int.TryParse(trace.FinalOutput, out int q) ? q : quality;
            return Task.FromResult<QualityScore?>(new QualityScore
            {
                Overall = overall,
                Rationale = "metered",
                Facets = [new FacetScore("Groundedness", overall, "metered")]
            });
        }
    }

    /// <summary>
    /// A fake agent service. The run's <c>FinalOutput</c> is the quality the judge should report — the
    /// baseline quality for the registered agent, the candidate quality for the temp slot
    /// ("__refinery/…"). It also emits one <c>llm-response</c> trace event carrying token counts into the
    /// active capture scope, so <see cref="EfficiencyExtractor"/> sees real tokens and the budget governor
    /// can be exercised.
    /// </summary>
    private sealed class FakeAgentService(
        RefineryCaptureBroker broker,
        int quality,
        int? candidateQuality = null,
        int inputTokens = 0,
        int outputTokens = 0) : IAgentService
    {
        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default)
        {
            bool isCandidate = agentName.StartsWith("__refinery/", StringComparison.Ordinal);
            int q = isCandidate && candidateQuality is { } cq ? cq : quality;

            // Emit a token-bearing llm-response into the per-run capture (matches the trace builder's parser).
            if (inputTokens > 0 || outputTokens > 0)
            {
                broker.Receive(new RlogEvent
                {
                    Identifier = TraceEventTypes.LlmResponse,
                    Object2 = $"{{\"inputTokens\":{inputTokens},\"outputTokens\":{outputTokens}}}",
                    Timestamp = DateTime.UtcNow
                });
            }

            return Task.FromResult(new AgentResult
            {
                FinalOutput = q.ToString(),
                ExitReason = AgentExitReason.Completed,
                TotalSteps = 1,
                SessionId = Guid.NewGuid().ToString("n"),
                StateHistory = ["start", "end"]
            });
        }

        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs, AgentRunContext ctx, CancellationToken token = default) =>
            Run(agentName, inputs, token);
        public Task<AgentResult> Run(string agentName, string input, CancellationToken token = default) =>
            Run(agentName, new Dictionary<string, object> { ["input"] = input }, token);
        public Task<string?> ToString(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default) =>
            Task.FromResult<string?>(quality.ToString());
        public Task<string?> ToString(string agentName, string input, CancellationToken token = default) =>
            Task.FromResult<string?>(quality.ToString());
    }

    /// <summary>An in-memory <see cref="IAgentManager"/> stub (only AddOrReplace is exercised by the loop).</summary>
    private sealed class FakeAgentManager : IAgentManager
    {
        private readonly Dictionary<string, AgentProfile> _agents = [];
        public Task LoadAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LoadDirectory(string rootDirectory) { }
        public AgentProfile? Get(string name) => _agents.GetValueOrDefault(name);
        public List<AgentProfile> GetAll() => _agents.Values.ToList();
        public void Add(AgentProfile agent) => _agents[agent.Name ?? ""] = agent;
        public void AddOrReplace(AgentProfile agent) => _agents[agent.Name ?? ""] = agent;
    }

    /// <summary>
    /// A stub <see cref="IInferService"/> for the pairwise judge: returns whichever of Output-A / Output-B
    /// is the larger integer (the fake outputs are quality numbers), so the higher-quality candidate wins
    /// regardless of the gate's A/B position swap.
    /// </summary>
    private sealed class StubPairwiseInfer : IInferService
    {
        public Task<T?> ToObject<T>(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null,
            string? modelName = null, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default)
        {
            string a = inputs?.FirstOrDefault(i => i.Label == "Output-A")?.Text ?? "0";
            string b = inputs?.FirstOrDefault(i => i.Label == "Output-B")?.Text ?? "0";
            int.TryParse(a.Trim(), out int av);
            int.TryParse(b.Trim(), out int bv);
            string winner = av == bv ? "tie" : (av > bv ? "a" : "b");
            JObject obj = new() { ["winner"] = winner, ["confidence"] = 90, ["rationale"] = "stub" };
            return Task.FromResult(obj.ToObject<T>());
        }

        public async Task<(T? Value, CompletionResult? Usage)> ToObjectWithUsage<T>(string promptName, List<Input>? inputs,
            ModelProfile? model = null, string? modelName = null, CancellationToken ct = default)
        {
            T? value = await ToObject<T>(promptName, inputs, model, modelName, token: ct);
            return (value, null); // stub reports no meta-token usage
        }

        public Task<T?> ToObject<T>(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CompletionResult?> Completion(Prompt prompt, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken token = default, bool directRoute = false) => throw new NotImplementedException();
        public Task<CompletionResult?> Completion(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken token = default) => throw new NotImplementedException();
        public IAsyncEnumerable<string> CompletionStream(Prompt prompt, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken cancellationToken = default, bool directRoute = false) => throw new NotImplementedException();
        public Task<TEnum> ToEnum<TEnum>(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, bool includeEnumValues = false, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default) where TEnum : struct, Enum => throw new NotImplementedException();
        public Task<TEnum> ToEnum<TEnum>(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, bool includeEnumValues = false, CancellationToken token = default) where TEnum : struct, Enum => throw new NotImplementedException();
        public Task<string?> ToString(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<string?> ToString(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<bool?> ToBool(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<bool?> ToBool(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<JObject?> ToJObject(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<JObject?> ToJObject(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringList(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringList(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListClean(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListClean(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListLimited(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, int? maxLines = null, Func<string, bool>? evaluator = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListLimited(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, int? maxLines = null, Func<string, bool>? evaluator = null, CancellationToken token = default) => throw new NotImplementedException();
        public Prompt FindPrompt(string name) => throw new NotImplementedException();
        public string? ListInputs(ModelProfile model, List<Input>? inputs) => throw new NotImplementedException();
    }
}
