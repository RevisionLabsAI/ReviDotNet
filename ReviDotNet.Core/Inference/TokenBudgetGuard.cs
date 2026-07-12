// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Reconciles the three token limits that meet at every inference call:
/// <list type="bullet">
/// <item><b>context-window</b> — the model's CONTEXT window (guards input size; pre-existing check).</item>
/// <item><b>output-capacity</b> — the model's real maximum OUTPUT per completion (capability).</item>
/// <item><b>output-budget</b> — the REQUESTED output ceiling (prompt setting or model forced override).</item>
/// </list>
/// A request that exceeds the output capability, or whose input + requested output overflows the context
/// window, would be rejected by the provider with an opaque 400 — this guard clamps it down instead and
/// logs what it did, so the call succeeds with the largest budget that actually fits.
/// </summary>
public static class TokenBudgetGuard
{
    /// <summary>
    /// Returns the effective max-tokens for a call: <paramref name="requested"/> clamped to the model's
    /// <see cref="ModelProfile.OutputCapacity"/> capability and to the remaining context window
    /// (<see cref="ModelProfile.ContextWindow"/> − <paramref name="estimatedInputTokens"/>). A null request
    /// passes through untouched (the provider default applies; there is nothing to clamp). Each clamp is
    /// logged once with the caller's <paramref name="promptName"/> for traceability.
    /// </summary>
    public static int? Clamp(int? requested, int estimatedInputTokens, ModelProfile model, string promptName)
    {
        if (requested is not { } value)
            return null;

        // (1) Output capability: never ask for more than the model can produce.
        if (model.OutputCapacity is { } cap && value > cap)
        {
            Util.Log($"TokenBudgetGuard: prompt '{promptName}' requested output-budget {value} > model '{model.Name}' " +
                     $"output-capacity {cap}; clamping to {cap}.");
            value = cap;
        }

        // (2) Context window: input + output must fit. Only meaningful when a context size is declared.
        if (model.ContextWindow > 0 && estimatedInputTokens + value > model.ContextWindow)
        {
            int room = Math.Max(1, model.ContextWindow - estimatedInputTokens);
            Util.Log($"TokenBudgetGuard: prompt '{promptName}' input (~{estimatedInputTokens} tokens) + requested " +
                     $"output-budget {value} exceeds model '{model.Name}' context window {model.ContextWindow}; " +
                     $"clamping output budget to {room}.");
            value = room;
        }

        return value;
    }
}
