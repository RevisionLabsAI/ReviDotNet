// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Api;

/// <summary>
/// Request payload for the embedding gateway (<c>POST /api/v1/embed</c>). Mirrors
/// <see cref="ForgeInferRequest"/>: the gateway picks an eligible embedding model
/// (honouring the routing hints below) and fails over across candidates.
/// </summary>
public record ForgeEmbedRequest
{
    public required string ClientId { get; init; }

    /// <summary>A single text to embed. Ignored when <see cref="Inputs"/> is non-empty.</summary>
    public string? Input { get; init; }

    /// <summary>A batch of texts to embed in one request.</summary>
    public List<string>? Inputs { get; init; }

    /// <summary>Minimum model tier to consider when routing automatically.</summary>
    public ModelTier? MinTier { get; init; }

    /// <summary>Ordered list of preferred model names; the first eligible one wins.</summary>
    public List<string>? PreferredModels { get; init; }

    /// <summary>Model names to exclude from routing.</summary>
    public List<string>? BlockedModels { get; init; }

    /// <summary>Pin the request to a specific embedding model, bypassing routing.</summary>
    public string? ExplicitModel { get; init; }

    /// <summary>Override the output vector dimensionality (if the model supports it).</summary>
    public int? Dimensions { get; init; }

    /// <summary>Override the encoding format (e.g. "float", "base64").</summary>
    public string? EncodingFormat { get; init; }
}
