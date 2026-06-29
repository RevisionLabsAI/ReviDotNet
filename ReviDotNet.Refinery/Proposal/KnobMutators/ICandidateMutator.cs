// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// A deterministic, typed knob mutator that performs a targeted text edit on an agent/prompt source to
/// address a specific class of weakness surfaced by a scoring round. Unlike <see cref="LlmDiffProposer"/>
/// these are cheap, predictable, and side-effect free — they never call an LLM. A mutator returns
/// <c>null</c> when its knob is absent from the source, or when no useful edit applies (it must never
/// produce a no-op or malformed revision).
/// </summary>
public interface ICandidateMutator
{
    /// <summary>The knob class this mutator edits (matches <see cref="Proposal.KnobType"/>).</summary>
    string KnobType { get; }

    /// <summary>
    /// Propose a single targeted revision to <paramref name="currentDefinition"/>, or <c>null</c> when the
    /// knob is not present/applicable.
    /// </summary>
    /// <param name="agentName">The agent being refined.</param>
    /// <param name="currentDefinition">The full current .agent/.pmt source.</param>
    /// <param name="scores">The aggregate scores for the round.</param>
    /// <param name="cards">The per-run score cards for the round.</param>
    Proposal? Mutate(string agentName, string currentDefinition, SuiteAggregate scores, IReadOnlyList<ScoreCard> cards);
}
