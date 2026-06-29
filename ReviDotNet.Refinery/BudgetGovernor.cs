// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Per-campaign token budget tracker. Accumulates AGENT-EXECUTION tokens (the sum of
/// <see cref="AgentTrace.InputTokens"/> + <see cref="AgentTrace.OutputTokens"/> charged via
/// <see cref="Record"/>) and reports whether the budget is exhausted.
/// <para>
/// <b>Known limitation:</b> the meta-LLM calls (judge / pairwise / proposer) go through
/// <c>IInferService.ToObject&lt;T&gt;</c>, which discards the underlying <c>CompletionResult</c>, so their
/// token usage is NOT observable and is therefore NOT counted here. The governor measures the cost of
/// running the agent under test only. Exact meta-LLM accounting via <c>IInferService.Completion</c> is a
/// future enhancement.
/// </para>
/// <para>
/// This type is intentionally NOT a DI singleton — a fresh instance is created per campaign
/// (<c>new BudgetGovernor(spec.TokenBudget)</c>) so its running total is scoped to that campaign. It is not
/// thread-safe; the Forge run gate serializes whole campaigns, so a single governor is only ever touched by
/// one campaign at a time.
/// </para>
/// </summary>
/// <param name="budget">The token budget in agent-execution tokens, or null for "unbounded".</param>
public sealed class BudgetGovernor(long? budget)
{
    private readonly long? _budget = budget;
    private long _spent;

    /// <summary>Total agent-execution tokens recorded so far.</summary>
    public long Spent => _spent;

    /// <summary>The configured token budget, or null when the campaign runs unbounded.</summary>
    public long? Budget => _budget;

    /// <summary>
    /// True once a budget is set and the recorded spend has met or exceeded it. Always false when no budget
    /// is configured (null) — an unbounded campaign never exhausts.
    /// </summary>
    public bool Exhausted => _budget is { } b && _spent >= b;

    /// <summary>
    /// Charge <paramref name="tokens"/> agent-execution tokens against the budget. Negative inputs are
    /// clamped to zero so a malformed trace can never decrease the running total.
    /// </summary>
    public void Record(long tokens)
    {
        if (tokens > 0)
            _spent += tokens;
    }
}
