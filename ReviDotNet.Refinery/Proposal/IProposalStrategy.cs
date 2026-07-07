// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>A proposed revision to an agent/prompt definition.</summary>
/// <param name="KnobType">The lever changed (system-prompt, few-shot, guardrail, …).</param>
/// <param name="RevisedContent">The full revised .agent/.pmt content.</param>
/// <param name="Diff">A standard unified diff (one full-context <c>@@</c> hunk) vs the current definition — human-readable AND machine re-appliable.</param>
/// <param name="Rationale">Why this change should help.</param>
public sealed record Proposal(string KnobType, string RevisedContent, string Diff, string Rationale);

/// <summary>
/// Produces candidate revisions to an agent given its current definition and the weaknesses surfaced by a
/// scoring round. Implementations may use an LLM diff proposer, typed knob mutators, or both.
/// (Phase 4 — the baseline controller runs without a proposer.)
/// </summary>
public interface IProposalStrategy
{
    /// <summary>Propose a revision; return null to indicate no useful change was found.</summary>
    Task<Proposal?> ProposeAsync(
        string agentName,
        string currentDefinition,
        SuiteAggregate scores,
        IReadOnlyList<ScoreCard> cards,
        CancellationToken ct = default);
}
