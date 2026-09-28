using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Revi;

/// <summary>Receives completed decisions without state or question bodies. Implementations must not block execution.</summary>
public interface IDecisionObserver
{
    /// <summary>Checks a host-owned execution budget before a provider request. May refuse the call.</summary>
    void Starting() { }
    /// <summary>Records versions, distributions, usage, and elapsed time.</summary>
    void Completed(DecisionRun run);
}

/// <summary>Resolves named question packs and decision model/provider profiles.</summary>
public sealed class DecisionService(IDecisionRegistry registry, IProviderManager providers,
    IEnumerable<IDecisionObserver> observers, IReviLogger<DecisionService> logger) : IDecisionService
{
    /// <inheritdoc/>
    public async Task<DecisionRun> EvaluateAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null)
    {
        token.ThrowIfCancellationRequested();
        DecisionPrompt prompt = registry.GetPrompt(promptName);
        DecisionModelProfile model = registry.GetModel(options?.Model ?? prompt.Model);
        ProviderProfile provider = providers.Get(model.ProviderName) ?? throw new InvalidOperationException($"Decision provider '{model.ProviderName}' is missing.");
        if (!model.Enabled || provider.Enabled == false) throw new InvalidOperationException("Decision model or provider is disabled.");
        IDecisionModelClient client = provider.DecisionClient ?? throw new InvalidOperationException("Provider does not expose a decision capability.");
        Dictionary<string, DecisionQuestion> questions = new(prompt.Questions, StringComparer.Ordinal);
        if (options?.Choices is not null)
            foreach (KeyValuePair<string, IReadOnlyDictionary<string, object?>> replacement in options.Choices)
            {
                if (!questions.TryGetValue(replacement.Key, out DecisionQuestion? question) || question is not ChoiceQuestion choice)
                    throw new ArgumentException("Dynamic choices must replace a declared Choice question.", nameof(options));
                questions[replacement.Key] = choice with { Options = replacement.Value };
            }
        DecisionRequest request = new(model.ModelString, state, questions);
        DecisionValidation.Validate(request, model.MaximumChoiceOptions, model.MaximumScoreLevels);
        // UTF-8 byte count is a conservative upper bound for common byte-tokenizing models, not a tokenizer.
        // A caller can set provider-specific limits; the provider still enforces its actual token budget.
        int stateSize = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(state));
        int[] sizes = questions.Values.Select(q => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { q.Kind, q.Instructions, q.Criteria }))).ToArray();
        if ((long)stateSize + sizes.Sum(s => (long)s) > model.ContextWindow || (long)stateSize + sizes.Max() > model.StatePlusLongestQuestion)
            throw new ArgumentException("Decision request exceeds the configured conservative context budget.");
        Stopwatch clock = Stopwatch.StartNew();
        foreach (IDecisionObserver observer in observers) observer.Starting();
        DecisionRun response = await client.EvaluateAsync(request, options, token).ConfigureAwait(false);
        DecisionRun run = response with
        {
            PromptName = prompt.Name, PromptVersion = prompt.Version, PromptHash = DecisionPrompt.ComputeHash(questions),
            ProviderName = model.ProviderName, ModelProfile = model.Name, Elapsed = clock.Elapsed,
            Cost = response.Usage.InputTokens * model.InputPerMillion / 1_000_000m + response.Usage.OutputTokens * model.OutputPerMillion / 1_000_000m
        };
        foreach (IDecisionObserver observer in observers)
        {
            try { observer.Completed(run); }
            catch
            {
                try { logger.LogWarning("A decision observer failed; the completed result is retained."); }
                catch { /* Optional logging cannot discard a completed result either. */ }
            }
        }
        try
        {
            logger.LogInfo($"Decision completed: id={run.Id} prompt={run.PromptName} version={run.PromptVersion} hash={run.PromptHash} provider={run.ProviderName} profile={run.ModelProfile} model={run.Model} inputTokens={run.Usage.InputTokens} outputTokens={run.Usage.OutputTokens} costUsd={run.Cost} elapsedMs={run.Elapsed.TotalMilliseconds:F0}",
                object1: run.Answers.ToDictionary(p => p.Key, p => (object)p.Value), object1Name: "decision-distributions");
        }
        catch { /* Logging must not discard completed results or prevent usage observers. */ }
        return run;
    }

    /// <inheritdoc/>
    public async Task<ChoiceAnswer<T>> ChoiceAsync<T>(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null) where T : struct, Enum
    {
        string id = SingleQuestion<ChoiceQuestion>(promptName);
        return (await EvaluateAsync(promptName, state, token, options).ConfigureAwait(false)).Choice<T>(id);
    }
    /// <inheritdoc/>
    public async Task<BooleanAnswer> BooleanAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null)
    {
        string id = SingleQuestion<BooleanQuestion>(promptName);
        return (await EvaluateAsync(promptName, state, token, options).ConfigureAwait(false)).Boolean(id);
    }
    /// <inheritdoc/>
    public async Task<ScoreAnswer> ScoreAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null)
    {
        string id = SingleQuestion<ScoreQuestion>(promptName);
        return (await EvaluateAsync(promptName, state, token, options).ConfigureAwait(false)).Score(id);
    }
    /// <summary>Checks convenience-method shape before making a billable request.</summary>
    private string SingleQuestion<T>(string promptName) where T : DecisionQuestion
    {
        DecisionPrompt prompt = registry.GetPrompt(promptName);
        if (prompt.Questions.Count != 1 || prompt.Questions.Single().Value is not T)
            throw new InvalidOperationException("Convenience methods require exactly one question of the requested type; use EvaluateAsync for batches.");
        return prompt.Questions.Single().Key;
    }
}
