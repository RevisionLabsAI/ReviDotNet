// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI-backed facade for provider lookups. Delegates to the legacy static ProviderManager
/// to keep compatibility while enabling DI consumption throughout the app.
/// </summary>
public sealed class ProviderRegistry : IProviderManager
{
    public ProviderProfile? Get(string name) => ProviderManager.Get(name);
}