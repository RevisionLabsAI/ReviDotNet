namespace Revi;

/// <summary>What the search can establish about its candidate set.</summary>
public enum DocumentSearchStatus
{
    /// <summary>No candidate passage was retrieved.</summary>
    NotFound,
    /// <summary>At least one passage satisfies the configured evidence policy.</summary>
    Found,
    /// <summary>Candidates exist but evidence is uncalibrated, insufficient, or a judge failed.</summary>
    NeedsMoreSearch
}

/// <summary>A source passage with independent semantic judgments.</summary>
public sealed record DocumentSearchHit(DocumentPassage Passage, double RetrievalScore, double? EvidenceProbability,
    double? ContradictionProbability, double? InstructionProbability, DecisionDisposition Disposition, DecisionRun? Decision);

/// <summary>Search outcome with exact sources and an explicit fallback state.</summary>
public sealed record DocumentSearchResult(IReadOnlyList<DocumentSearchHit> Hits, DocumentSearchStatus Status,
    int CandidateCount, string? FallbackReason);

/// <summary>Bounds retrieval and optional semantic evaluation.</summary>
public sealed record DocumentSearchOptions
{
    /// <summary>Number of retrieved candidates sent to the judge at most.</summary>
    public int CandidateCount { get; init; } = 20;
    /// <summary>Number of returned passages at most.</summary>
    public int ResultCount { get; init; } = 8;
    /// <summary>Named decision pack, or null for lexical/vector retrieval only.</summary>
    public string? DecisionPrompt { get; init; } = "document-passage-fit";
    /// <summary>Decision model override.</summary>
    public string? DecisionModel { get; init; }
    /// <summary>Optional calibrated policy for evidence acceptance.</summary>
    public DecisionPolicy? EvidencePolicy { get; init; }
    /// <summary>Bounded parallel query-passage calls. Independent questions about one passage share a request.</summary>
    public int MaximumConcurrency { get; init; } = 4;
    /// <summary>Overall search deadline.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Hybrid retrieval followed by decision reranking over an explicit collection.</summary>
public interface IDocumentSearchService
{
    /// <summary>Searches only the supplied collection; never discovers a global current collection.</summary>
    Task<DocumentSearchResult> SearchAsync(DocumentCollection collection, string query, DocumentSearchOptions? options = null, CancellationToken token = default);
}

/// <summary>Evidence-preserving search. Retrieved content is untrusted even when instruction probability is low.</summary>
public sealed class DocumentSearchService(IDecisionService decisions, IEmbedService embeddings) : IDocumentSearchService
{
    /// <inheritdoc/>
    public async Task<DocumentSearchResult> SearchAsync(DocumentCollection collection, string query, DocumentSearchOptions? options = null, CancellationToken token = default)
    {
        options ??= new DocumentSearchOptions();
        if (options.EvidencePolicy is not null && options.EvidencePolicy.Question != "evidence")
            throw new ArgumentException("Document evidence policies must target the evidence question.", nameof(options));
        if (options.CandidateCount is < 1 or > 255 || options.ResultCount < 1 || options.ResultCount > options.CandidateCount || options.MaximumConcurrency is < 1 or > 32 || options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) return new([], DocumentSearchStatus.NotFound, 0, null);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.Timeout);
        CancellationToken ct = deadline.Token;
        float[]? vector = null;
        string? fallback = null;
        if (collection.EmbeddingModel is not null)
        {
            try { vector = await embeddings.Generate(query, collection.EmbeddingModel, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { fallback = "embedding-timeout"; }
            catch (Exception ex) when (ex is not OperationCanceledException) { fallback = "embedding-unavailable"; }
            if (vector is null) fallback ??= "embedding-unavailable";
        }
        IReadOnlyList<(DocumentPassage Passage, double Score)> candidates = collection.Retrieve(query, options.CandidateCount, vector);
        if (candidates.Count == 0) return new([], DocumentSearchStatus.NotFound, 0, fallback);
        DocumentSearchHit[] baseline = candidates.Select(c => new DocumentSearchHit(c.Passage, c.Score, null, null, null, DecisionDisposition.Review, null)).ToArray();
        if (options.DecisionPrompt is null) return new(baseline.Take(options.ResultCount).ToArray(), DocumentSearchStatus.NeedsMoreSearch, candidates.Count, fallback);
        using SemaphoreSlim gate = new(options.MaximumConcurrency);
        DocumentSearchHit[] evaluated = await Task.WhenAll(baseline.Select(async hit =>
        {
            bool entered = false;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                entered = true;
                DecisionRun run = await decisions.EvaluateAsync(options.DecisionPrompt,
                    new { query, passage = hit.Passage.Text }, ct, new DecisionOptions { Model = options.DecisionModel, RetryLimit = 0, Timeout = options.Timeout }).ConfigureAwait(false);
                _ = run.Boolean("relevant");
                return hit with
                {
                    EvidenceProbability = run.Boolean("evidence").Probability,
                    ContradictionProbability = run.Boolean("contradicts").Probability,
                    InstructionProbability = run.Boolean("instructions").Probability,
                    Disposition = options.EvidencePolicy?.Evaluate(run) ?? DecisionDisposition.Review, Decision = run
                };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return hit; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return hit; }
            finally { if (entered) gate.Release(); }
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Partial batches preserve the complete retrieval ranking; a failed candidate is not silently demoted.
        bool incomplete = evaluated.Any(h => h.Decision is null);
        IEnumerable<DocumentSearchHit> ranked = incomplete ? evaluated : evaluated.OrderByDescending(h => h.EvidenceProbability).ThenByDescending(h => h.RetrievalScore).ThenBy(h => h.Passage.Id, StringComparer.Ordinal);
        DocumentSearchHit[] hits = ranked.Take(options.ResultCount).ToArray();
        return new(hits, !incomplete && hits.Any(h => h.Disposition == DecisionDisposition.Accept) ? DocumentSearchStatus.Found : DocumentSearchStatus.NeedsMoreSearch,
            candidates.Count, incomplete ? "decision-incomplete-or-unavailable" : fallback);
    }
}
