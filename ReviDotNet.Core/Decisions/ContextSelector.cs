namespace Revi;

/// <summary>Sanitized metadata for an already-authorized tool, skill, or resource.</summary>
public sealed record ContextCandidate(string Id, string Description, string Kind = "tool", bool Required = false)
{
    /// <summary>Optional examples of when to select this capability; never credentials or runtime arguments.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
}

/// <summary>Opt-in bounded context selection.</summary>
public sealed record ContextSelectionOptions
{
    /// <summary>Named question pack containing rank (Choice) and any-fit (Boolean).</summary>
    public string Prompt { get; init; } = "context-select";
    /// <summary>Optional model override.</summary>
    public string? Model { get; init; }
    /// <summary>Maximum optional candidates. Required candidates are retained in addition.</summary>
    public int MaximumSelected { get; init; } = 3;
    /// <summary>Hard request deadline; selection never retries.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(750);
    /// <summary>Optional calibrated gate. Without a gate, selection only ranks; it does not assert no-match.</summary>
    public DecisionPolicy? FitPolicy { get; init; }
}

/// <summary>Selected metadata and an observable fallback reason, without request text.</summary>
public sealed record ContextSelectionResult(IReadOnlyList<ContextCandidate> Candidates, string? FallbackReason, DecisionRun? Decision);

/// <summary>Reduces visible context from a caller-authorized candidate set. Never grants permissions.</summary>
public interface IContextSelector
{
    /// <summary>Returns a safe per-run cache key, or null when model version stability is unknown.</summary>
    string? GetCacheKey(object state, IReadOnlyList<ContextCandidate> candidates, ContextSelectionOptions options) => null;
    /// <summary>Selects context or returns the complete authorized catalog on operational failure.</summary>
    Task<ContextSelectionResult> SelectAsync(object state, IReadOnlyList<ContextCandidate> authorizedCandidates,
        ContextSelectionOptions options, CancellationToken token = default);
}

/// <summary>Conservative decision-backed selection shared by tools, skills, and resources.</summary>
public sealed class DecisionContextSelector(IDecisionService decisions, IDecisionRegistry registry) : IContextSelector
{
    /// <inheritdoc/>
    public string? GetCacheKey(object state, IReadOnlyList<ContextCandidate> candidates, ContextSelectionOptions options)
    {
        try
        {
            DecisionPrompt prompt = registry.GetPrompt(options.Prompt);
            DecisionModelProfile model = registry.GetModel(options.Model ?? prompt.Model);
            if (!model.RevisionPinned) return null;
            string content = System.Text.Json.JsonSerializer.Serialize(new { state, candidates, options, prompt.Version, prompt.Hash, model.ModelString, model.ProviderName });
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        }
        catch { return null; }
    }
    /// <inheritdoc/>
    public async Task<ContextSelectionResult> SelectAsync(object state, IReadOnlyList<ContextCandidate> authorizedCandidates,
        ContextSelectionOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (options.MaximumSelected < 0 || options.Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        ContextCandidate[] candidates = authorizedCandidates.ToArray();
        if (candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
            throw new ArgumentException("Candidate IDs must be unique.");
        if (candidates.Length == 0) return new([], null, null);
        DecisionRun? run = null;
        // The deadline is enforced here, not left to the transport honouring DecisionOptions.Timeout: a host's
        // decision service or model client may ignore it, and the agent step waits on this call.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.Timeout);
        try
        {
            DecisionPrompt prompt = registry.GetPrompt(options.Prompt);
            DecisionModelProfile model = registry.GetModel(options.Model ?? prompt.Model);
            if (candidates.Length > model.MaximumChoiceOptions)
                return new(candidates, "candidate-limit", null);
            Dictionary<string, object?> criteria = candidates.ToDictionary(c => c.Id,
                c => (object?)new { description = c.Description, kind = c.Kind, examples = c.Examples }, StringComparer.Ordinal);
            run = await decisions.EvaluateAsync(options.Prompt, new { task = state, candidates = criteria }, deadline.Token, new DecisionOptions
            {
                Model = options.Model, Timeout = options.Timeout, RetryLimit = 0,
                Choices = new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["rank"] = criteria }
            }).ConfigureAwait(false);
            if (model.RevisionPinned && run.Model != model.ModelString) return new(candidates, "model-revision-mismatch", run);
            ChoiceAnswer rank = run.Choice("rank");
            _ = run.Boolean("any-fit");
            if (rank.Probabilities.Count != candidates.Length || candidates.Any(c => !rank.Probabilities.ContainsKey(c.Id)))
                return new(candidates, "incomplete-catalog-answer", run);
            if (options.FitPolicy is not null)
            {
                if (options.FitPolicy.Question != "any-fit") return new(candidates, "invalid-fit-policy", run);
                DecisionDisposition disposition = options.FitPolicy.Evaluate(run);
                if (disposition == DecisionDisposition.Review) return new(candidates, "fit-needs-review", run);
                if (disposition == DecisionDisposition.Reject) return new(candidates.Where(c => c.Required).ToArray(), null, run);
            }
            HashSet<string> selected = candidates.Where(c => !c.Required)
                .OrderByDescending(c => rank.Probabilities[c.Id]).ThenBy(c => c.Id, StringComparer.Ordinal)
                .Take(options.MaximumSelected).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            return new(candidates.Where(c => c.Required || selected.Contains(c.Id)).ToArray(), null, run);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(candidates, "selector-timeout", run); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(candidates, "selector-unavailable", run); }
    }
}
