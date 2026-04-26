// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using ReviDotNet.Forge.Models;

namespace ReviDotNet.Forge.Services.ApiKeys;

public interface IForgeApiKeyService
{
    Task<List<ForgeApiKey>> GetAllAsync();
    Task<(ForgeApiKey Key, string RawKey)> CreateAsync(string clientId);
    Task<bool> SetEnabledAsync(string id, bool enabled);
    Task<bool> DeleteAsync(string id);
    Task<string?> ValidateAsync(string rawKey);
}
