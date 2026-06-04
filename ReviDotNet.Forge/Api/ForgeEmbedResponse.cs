// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace ReviDotNet.Forge.Api;

/// <summary>
/// Response payload for the embedding gateway (<c>POST /api/v1/embed</c>).
/// <see cref="Embeddings"/> preserves input order, one vector per input.
/// </summary>
public record ForgeEmbedResponse
{
    public bool Success { get; init; }
    public List<float[]>? Embeddings { get; init; }
    public string ModelUsed { get; init; } = string.Empty;
    public string ProviderUsed { get; init; } = string.Empty;
    public int? Dimensions { get; init; }
    public int? InputTokens { get; init; }
    public string? ErrorMessage { get; init; }
}
