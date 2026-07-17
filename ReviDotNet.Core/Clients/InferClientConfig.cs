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
    /// How JSON guidance is expressed on the wire for OpenAI-protocol providers: strict
    /// <c>json_schema</c> (default) or the <c>json_object</c> downgrade for hosts that reject the
    /// schema form (e.g. Z.ai/GLM). Ignored by other protocols.
    /// </summary>
    public JsonSchemaMode JsonSchemaMode { get; set; } = JsonSchemaMode.JsonSchema;

    /// <summary>
    /// The version segment prepended to OpenAI-style endpoint paths ("v1" → "v1/chat/completions").
    /// Empty string means no segment ("chat/completions"), for hosts whose base URL already carries
    /// the full version path (e.g. Z.ai's https://api.z.ai/api/paas/v4/). Normalized (no slashes) by
    /// the <see cref="InferClient"/> constructor. Not used by the Gemini/Claude protocols.
    /// </summary>
    public string ApiVersionPath { get; set; } = "v1";

    /// <summary>
    /// Inactivity timeout for non-responsive providers (seconds).
    /// </summary>
    public int InactivityTimeoutSeconds { get; set; } = 60;
}