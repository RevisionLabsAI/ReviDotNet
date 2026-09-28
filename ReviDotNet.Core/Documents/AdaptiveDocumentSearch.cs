using System.Diagnostics;

namespace Revi;

/// <summary>Code-owned limits and calibrated stopping rules for adaptive retrieval.</summary>
public sealed record AdaptiveSearchOptions
{
    /// <summary>Underlying search settings.</summary>
    public DocumentSearchOptions Search { get; init; } = new();
    /// <summary>Maximum retrieval rounds, including the first.</summary>
    public int MaximumRounds { get; init; } = 3;
    /// <summary>Worst-case allowed decision calls, including passage judgments and stopping checks.</summary>
    public int MaximumDecisionCalls { get; init; } = 80;
    /// <summary>Overall deadline across all rounds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Question pack for evidence sufficiency, contradictions and search utility.</summary>
    public string StopPrompt { get; init; } = "research-stop";
    /// <summary>Calibrated sufficiency question policy.</summary>
    public required DecisionPolicy Sufficiency { get; init; }
    /// <summary>Calibrated unresolved-contradiction question policy.</summary>
    public required DecisionPolicy Contradictions { get; init; }
    /// <summary>Calibrated additional-search-utility question policy.</summary>
    public required DecisionPolicy SearchUtility { get; init; }
}

/// <summary>An adaptive retrieval trace without copied source bodies beyond the returned search result.</summary>
public sealed record AdaptiveSearchResult(DocumentSearchResult Search, int Rounds, int ReservedDecisionCalls,
    string StopReason, IReadOnlyList<DecisionRun> StopDecisions, TimeSpan Elapsed);

/// <summary>Expands retrieval depth while code retains control of deadlines and request budgets.</summary>
public sealed class AdaptiveDocumentSearch(IDocumentSearchService search, IDecisionService decisions)
{
    /// <summary>Runs bounded retrieval; the model can advise stopping but cannot increase limits.</summary>
    public async Task<AdaptiveSearchResult> SearchAsync(DocumentCollection collection, string query, AdaptiveSearchOptions options, CancellationToken token = default)
    {
        if (options.MaximumRounds is < 1 or > 10 || options.MaximumDecisionCalls < 1 || options.Timeout <= TimeSpan.Zero ||
            options.Search.CandidateCount is < 1 or > 255 || options.Search.ResultCount < 1 || options.Search.ResultCount > options.Search.CandidateCount)
            throw new ArgumentOutOfRangeException(nameof(options));
        options.Sufficiency.Validate(); options.Contradictions.Validate(); options.SearchUtility.Validate();
        // Checked here too: inside the round loop the search's own refusal would be reported as "judge-unavailable".
        DocumentSearchService.ValidateEvidencePolicy(options.Search.EvidencePolicy);
        if (options.Sufficiency.Question != "sufficient" || options.Contradictions.Question != "unresolved" || options.SearchUtility.Question != "more-useful")
            throw new ArgumentException("Stopping policies must target sufficient, unresolved, and more-useful respectively.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.Timeout);
        Stopwatch elapsed = Stopwatch.StartNew();
        List<DecisionRun> checks = [];
        DocumentSearchResult latest = new([], DocumentSearchStatus.NeedsMoreSearch, 0, "not-searched");
        int rounds = 0, calls = 0;
        string reason = "round-limit";
        try
        {
            for (int round = 0; round < options.MaximumRounds; round++)
            {
                int candidateCount = Math.Min(255, checked(options.Search.CandidateCount * (round + 1)));
                int requiredCalls = (options.Search.DecisionPrompt is null ? 0 : candidateCount) + 1;
                if (calls + requiredCalls > options.MaximumDecisionCalls) { reason = "call-limit"; break; }
                calls += requiredCalls;
                latest = await search.SearchAsync(collection, query, options.Search with
                {
                    CandidateCount = candidateCount, ResultCount = Math.Min(candidateCount, options.Search.ResultCount * (round + 1))
                }, deadline.Token).ConfigureAwait(false);
                rounds++;
                // Bound the evidence supplied to the stopping model. Truncation is explicit in state.
                int remaining = 12000;
                List<object> evidence = [];
                foreach (DocumentSearchHit hit in latest.Hits)
                {
                    if (remaining <= 0) break;
                    string text = hit.Passage.Text[..Math.Min(hit.Passage.Text.Length, remaining)];
                    evidence.Add(new { id = hit.Passage.Id, text, truncated = text.Length != hit.Passage.Text.Length });
                    remaining -= text.Length;
                }
                DecisionRun check = await decisions.EvaluateAsync(options.StopPrompt, new { query, evidence, candidateCount = latest.CandidateCount },
                    deadline.Token, new DecisionOptions { Model = options.Search.DecisionModel, RetryLimit = 0 }).ConfigureAwait(false);
                checks.Add(check);
                if (latest.Status == DocumentSearchStatus.Found && options.Sufficiency.Evaluate(check) == DecisionDisposition.Accept &&
                    options.Contradictions.Evaluate(check) == DecisionDisposition.Reject && options.SearchUtility.Evaluate(check) == DecisionDisposition.Reject)
                { reason = "sufficient-evidence"; break; }
                // Retrieval returns only matching passages (BM25 score > 0), so a short round means a deeper
                // round would refetch and re-judge the same set: stop instead of paying for it again.
                if (latest.CandidateCount < candidateCount) { reason = "retrieval-exhausted"; break; }
                if (candidateCount >= collection.Passages.Count || candidateCount == 255) { reason = "candidate-limit"; break; }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { reason = "deadline"; }
        catch (Exception ex) when (ex is not OperationCanceledException) { reason = "judge-unavailable"; }
        token.ThrowIfCancellationRequested();
        return new(latest, rounds, calls, reason, checks, elapsed.Elapsed);
    }
}
