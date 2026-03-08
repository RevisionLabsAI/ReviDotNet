// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI-backed facade for prompt lookups. Delegates to the legacy static PromptManager
/// to keep compatibility while enabling DI consumption throughout the app.
/// </summary>
public sealed class PromptRegistry : IPromptManager
{
    public Prompt? Get(string name) => PromptManager.Get(name);
}