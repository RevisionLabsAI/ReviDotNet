// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

namespace Revi;

/// <summary>
/// DI-backed facade for provider lookups. Delegates to the legacy static ProviderManager
/// to keep compatibility while enabling DI consumption throughout the app.
/// </summary>
public sealed class ProviderRegistry : IProviderManager
{
    public ProviderProfile? Get(string name) => ProviderManager.Get(name);
}