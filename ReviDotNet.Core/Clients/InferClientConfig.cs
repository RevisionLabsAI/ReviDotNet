// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Configuration settings for the inference client.
/// </summary>
public class InferClientConfig
{
    /// <summary>
    /// The base URL of the inference API.
    /// </summary>
    public string ApiUrl { get; set; }

    /// <summary>
    /// The API key used for authentication, if required.
    /// </summary>
    public string ApiKey { get; set; }

    /// <summary>
    /// Whether an API key should be applied to requests.
    /// </summary>
    public bool UseApiKey { get; set; }

    /// <summary>
    /// The protocol/provider used by the client (e.g., OpenAI, Gemini, Claude).
    /// </summary>
    public Protocol Protocol { get; set; }

    /// <summary>
    /// The default model identifier to use for requests.
    /// </summary>
    public string DefaultModel { get; set; }

    /// <summary>
    /// Overall request timeout in seconds for non-streaming operations.
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Delay between requests in milliseconds to avoid provider throttling.
    /// </summary>
    public int DelayBetweenRequestsMs { get; set; }

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int RetryAttemptLimit { get; set; }

    /// <summary>
    /// Initial delay in seconds before retrying a failed request (exponential backoff applies).
    /// </summary>
    public int RetryInitialDelaySeconds { get; set; }

    /// <summary>
    /// Maximum number of simultaneous in-flight requests.
    /// </summary>
    public int SimultaneousRequests { get; set; }

    /// <summary>
    /// Whether the provider supports legacy completion endpoints.
    /// </summary>
    public bool SupportsCompletion { get; set; }

    /// <summary>
    /// Indicates whether the provider/model supports the newer Responses API for completions.
    /// When true and the protocol supports it (e.g., OpenAI), the client will use /v1/responses.
    /// </summary>
    public bool SupportsResponseCompletion { get; set; }

    /// <summary>
    /// Whether tool-assisted guidance is supported.
    /// </summary>
    public bool SupportsGuidance { get; set; }

    /// <summary>
    /// Optional default guidance type.
    /// </summary>
    public GuidanceType? DefaultGuidanceType { get; set; }

    /// <summary>
    /// Optional default guidance instruction string.
    /// </summary>
    public string? DefaultGuidanceString { get; set; }

    /// <summary>
    /// Inactivity timeout for non-responsive providers (seconds).
    /// </summary>
    public int InactivityTimeoutSeconds { get; set; } = 60;
}