// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net.Http.Json;

namespace Revi;

/// <summary>
/// Represents a usage report sent from a client that bypassed Forge routing and called a provider directly.
/// Mirrors the fields Forge needs to populate a <see cref="ReviDotNet.Forge.Models.ForgeUsageRecord"/>.
/// </summary>
internal record ForgeDirectUsageReport
{
    /// <summary>The client identifier from forge.rcfg.</summary>
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

    /// <summary>Input (prompt) token count. Zero if not available.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output (completion) token count. Zero if not available.</summary>
    public int OutputTokens { get; init; }

    /// <summary>End-to-end latency in milliseconds.</summary>
    public long LatencyMs { get; init; }

    /// <summary>Whether the request used streaming.</summary>
    public bool WasStreaming { get; init; }
}

/// <summary>
/// Sends fire-and-forget usage reports to Forge for requests that were handled with direct routing.
/// </summary>
public class ForgeReporter : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new <see cref="ForgeReporter"/>.
    /// </summary>
    /// <param name="forgeUrl">The base URL of the Forge server.</param>
    /// <param name="apiKey">The API key for authenticating with Forge.</param>
    public ForgeReporter(string forgeUrl, string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(forgeUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.Add("X-Forge-ApiKey", apiKey);
    }

    /// <summary>
    /// Posts a usage report to Forge in a fire-and-forget manner. Failures are silently swallowed.
    /// </summary>
    /// <param name="report">The usage data to report.</param>
    internal void ReportAndForget(ForgeDirectUsageReport report)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _http.PostAsJsonAsync("api/v1/usage/report", report);
            }
            catch { }
        });
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
