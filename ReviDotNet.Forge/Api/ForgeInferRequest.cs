// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Api;

public record ForgeInferRequest
{
    public required string ClientId { get; init; }
    public string? PromptName { get; init; }
    public string? PromptContent { get; init; }
    public List<ForgeInput>? Inputs { get; init; }
    public ModelTier? MinTier { get; init; }
    public List<string>? PreferredModels { get; init; }
    public List<string>? BlockedModels { get; init; }
    public string? ExplicitModel { get; init; }
    public bool Stream { get; init; } = true;
    public string? GuidanceSchema { get; init; }
    public CompletionType? CompletionType { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? InactivityTimeoutSeconds { get; init; }
}

public record ForgeInput(string Label, string Text);
