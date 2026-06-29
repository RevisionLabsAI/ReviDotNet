// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Shared, dependency-free text helpers for the typed knob mutators. Diffing reuses the same unified
/// line-diff algorithm as <see cref="LlmDiffProposer"/> so revisions render consistently for review.
/// </summary>
internal static class MutatorText
{
    /// <summary>Compute the unified line-diff vs the current definition (delegates to the proposer's helper).</summary>
    public static string Diff(string oldText, string newText) => LlmDiffProposer.UnifiedDiff(oldText, newText);
}
