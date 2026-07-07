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
    private static readonly AsyncLocal<Accumulator?> Current = new();

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
    }

    /// <summary>Total meta-LLM tokens recorded in the current scope (0 when no scope is open).</summary>
    public long Spent => Current.Value is { } acc ? Interlocked.Read(ref acc.Spent) : 0;

    private void End() => Current.Value = null;

    /// <summary>The mutable running total for one async flow.</summary>
    private sealed class Accumulator
    {
        public long Spent;
    }

    /// <summary>A scoped meta-usage accumulation; disposal stops routing to it.</summary>
    public sealed class Scope(MetaLlmUsageBroker broker) : IDisposable
    {
        private readonly MetaLlmUsageBroker _broker = broker;

        /// <inheritdoc/>
        public void Dispose() => _broker.End();
    }
}
