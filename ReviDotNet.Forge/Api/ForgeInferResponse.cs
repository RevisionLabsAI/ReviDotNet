// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace ReviDotNet.Forge.Api;

public record ForgeInferResponse
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string ModelUsed { get; init; } = string.Empty;
    public string ProviderUsed { get; init; } = string.Empty;
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public string? ErrorMessage { get; init; }
}
