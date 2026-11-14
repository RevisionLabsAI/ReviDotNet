// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Decides the reasoning ("thinking") value sent with a model call. Prompt calls and agent steps both
/// resolve through here, so the same model configuration produces the same request either way.
/// </summary>
public static class ThinkingSettings
{
    /// <summary>
    /// Resolves the provider-specific thinking value for one call.
    /// </summary>
    /// <param name="requested">The task's own amount — a prompt's or an agent state's <c>thinking</c>
    /// setting — or null/blank when it sets none.</param>
    /// <param name="model">The model the call goes to. Its <c>thinking</c> default applies when the task
    /// sets none, and its conversion table translates the common word to the provider's value.</param>
    /// <returns>The value to send, or null to send none.</returns>
    public static string? Resolve(string? requested, ModelProfile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? amount = !string.IsNullOrWhiteSpace(requested) ? requested : model.Thinking;
        return model.ResolveThinking(amount);
    }
}
