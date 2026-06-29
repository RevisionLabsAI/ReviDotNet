// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// The registry of deterministic typed knob mutators. Each <see cref="ICandidateMutator"/> performs a cheap,
/// targeted edit on an agent/prompt source to address one class of weakness, returning <c>null</c> when its
/// knob is absent or no useful change applies.
/// </summary>
public static class KnobMutators
{
    /// <summary>One instance of each typed mutator, in priority order.</summary>
    public static IReadOnlyList<ICandidateMutator> All() =>
    [
        new SamplingMutator(),
        new GuardrailMutator(),
        new SystemPromptMutator()
    ];
}
