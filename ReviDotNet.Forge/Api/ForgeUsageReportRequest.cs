// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace ReviDotNet.Forge.Api;

/// <summary>
/// Payload sent by clients that used direct routing instead of the Forge gateway.
/// Forge records this to its usage dashboard so telemetry remains complete.
/// </summary>
public record ForgeUsageReportRequest
{
    /// <summary>The client identifier from the client's forge.rcfg.</summary>
    public required string ClientId { get; init; }

    /// <summary>The prompt name that drove the request, if any.</summary>
    public string? PromptName { get; init; }

    /// <summary>The model that was actually used for inference.</summary>
    public required string ModelName { get; init; }

    /// <summary>The provider that was actually used for inference.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Whether the inference succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Reason for failure if <see cref="Success"/> is false.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Input (prompt) token count.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output (completion) token count.</summary>
    public int OutputTokens { get; init; }

    /// <summary>End-to-end latency in milliseconds as measured by the client.</summary>
    public long LatencyMs { get; init; }

    /// <summary>Whether the request used streaming.</summary>
    public bool WasStreaming { get; init; }
}
