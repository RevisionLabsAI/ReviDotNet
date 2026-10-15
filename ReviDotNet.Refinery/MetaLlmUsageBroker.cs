// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Accumulates the token usage of the meta-LLM calls (judge / pairwise gate / proposer) for the
/// <i>current async flow</i>, so a campaign can bound its own meta spend independently of the
/// agent-execution <see cref="BudgetGovernor"/>.
/// <para>
/// Mirrors <see cref="RefineryCaptureBroker"/>: a campaign opens a scope with <see cref="BeginScope"/>;
/// the three meta-LLM drivers call <see cref="Record"/> with the <see cref="Revi.CompletionResult"/>
/// returned by <c>IInferService.ToObjectWithUsage</c>. The running total for the active scope is read via
/// <see cref="Spent"/>. Concurrent campaigns live in different async contexts (separate accumulators), while
/// a campaign's nested meta calls share its context (same accumulator).
/// </para>
/// <para>
/// This is a DI singleton, but its state is per-async-flow via an <see cref="AsyncLocal{T}"/>, so the single
/// shared instance is safe across concurrent campaigns. Within a scope, <see cref="Record"/> uses
/// <see cref="Interlocked"/> so the total stays correct even if scoring is ever parallelized inside one
/// campaign. Outside any scope, <see cref="Record"/> is a no-op and <see cref="Spent"/> is 0.
/// </para>
/// </summary>
public sealed class MetaLlmUsageBroker
{
    /// <summary>
    /// The rate a meta call is priced at, per million input tokens, when its model has no configured
    /// rate. Deliberately high (the top of current list prices), so an unpriced model makes a dollar cap
    /// stop early rather than late.
    /// </summary>
    public const decimal FallbackCostPerMillionInputTokens = 10m;

    /// <summary>The output-token counterpart of <see cref="FallbackCostPerMillionInputTokens"/>.</summary>
    public const decimal FallbackCostPerMillionOutputTokens = 50m;

    private static readonly AsyncLocal<Accumulator?> Current = new();

    private readonly IModelManager? _models;

    /// <summary>Creates the broker.</summary>
    /// <param name="models">Where a meta call's model rates are looked up to price it; null prices every
    /// call at the fallback rates.</param>
    public MetaLlmUsageBroker(IModelManager? models = null) => _models = models;

    /// <summary>Begin accumulating meta-LLM tokens into a fresh counter for the current async flow.</summary>
    public Scope BeginScope()
    {
        Accumulator accumulator = new();
        Current.Value = accumulator;
        return new Scope(this);
    }

    /// <summary>
    /// Charge the input + output tokens carried by <paramref name="usage"/> against the active scope, if any.
    /// Null usage, missing token counts, and "no active scope" are all silently ignored.
    /// </summary>
    public void Record(Revi.CompletionResult? usage)
    {
        if (usage is null) return;
        Accumulator? acc = Current.Value;
        if (acc is null) return;

        long tokens = (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0);
        if (tokens > 0)
            Interlocked.Add(ref acc.Spent, tokens);

        decimal cost = Price(usage);
        if (cost > 0)
            Interlocked.Add(ref acc.SpentMicroUsd, (long)Math.Ceiling(cost * 1_000_000m));
    }

    /// <summary>Meta-LLM spend in the current scope, in USD (0 when no scope is open).</summary>
    public decimal SpentUsd => Current.Value is { } acc ? Interlocked.Read(ref acc.SpentMicroUsd) / 1_000_000m : 0m;

    /// <summary>
    /// Prices one meta call from its model's configured rates, falling back to the deliberately high
    /// <see cref="FallbackCostPerMillionInputTokens"/> / <see cref="FallbackCostPerMillionOutputTokens"/> for a
    /// model with no rates or one the registry does not know.
    /// </summary>
    /// <param name="usage">The call's completion, carrying its model and token counts.</param>
    /// <returns>The cost in USD.</returns>
    public decimal Price(Revi.CompletionResult usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ModelProfile? model = string.IsNullOrWhiteSpace(usage.Model) ? null : _models?.Get(usage.Model);
        decimal input = model?.CostPerMillionInputTokens ?? FallbackCostPerMillionInputTokens;
        decimal output = model?.CostPerMillionOutputTokens ?? FallbackCostPerMillionOutputTokens;
        return (usage.InputTokens ?? 0) / 1_000_000m * input + (usage.OutputTokens ?? 0) / 1_000_000m * output;
    }

    /// <summary>Total meta-LLM tokens recorded in the current scope (0 when no scope is open).</summary>
    public long Spent => Current.Value is { } acc ? Interlocked.Read(ref acc.Spent) : 0;

    private void End() => Current.Value = null;

    /// <summary>The mutable running total for one async flow.</summary>
    private sealed class Accumulator
    {
        public long Spent;
        public long SpentMicroUsd;
    }

    /// <summary>A scoped meta-usage accumulation; disposal stops routing to it.</summary>
    public sealed class Scope(MetaLlmUsageBroker broker) : IDisposable
    {
        private readonly MetaLlmUsageBroker _broker = broker;

        /// <inheritdoc/>
        public void Dispose() => _broker.End();
    }
}
