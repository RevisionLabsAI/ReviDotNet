// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Runtime.InteropServices.ObjectiveC;
using Newtonsoft.Json;
using Revi;

// ReSharper disable InconsistentNaming

namespace Revi;

public class ProviderProfile : IDisposable
{
    // ===================================
    //  ProviderProfile Object Definition
    // ===================================
    
    // Identifier
    [RConfigProperty("general_name")] 
    public string? Name { get; set; }
    
    [RConfigProperty("general_enabled")]
    public bool? Enabled { get; set; }
    
    public InferClient? InferenceClient;
    public EmbedClient? EmbeddingClient;

    /// <summary>Decision capability, independent of inference and embedding clients.</summary>
    public IDecisionModelClient? DecisionClient { get; set; }
    private IDisposable? _ownedDecisionClient;

    /// <summary>Releases only decision transports created by this profile; injected clients belong to their host.</summary>
    public void Dispose()
    {
        _ownedDecisionClient?.Dispose();
        if (ReferenceEquals(DecisionClient, _ownedDecisionClient)) DecisionClient = null;
        _ownedDecisionClient = null;
    }

    /// <summary>Environment variable to read when api-key is environment; defaults to PROVAPIKEY__NAME.</summary>
    [RConfigProperty("general_api-key-environment")]
    public string? APIKeyEnvironment { get; set; }
    
    [RConfigProperty("general_protocol")]
    public Protocol? Protocol { get; set; }
    
    // API Settings
    [RConfigProperty("general_api-url")]
    public string? APIURL { get; set; }

    [RConfigProperty("general_api-key")]
    public string? APIKey { get; set; }

    /// <summary>
    /// Version segment for OpenAI-style endpoints. Unset means the standard "v1"
    /// ("v1/chat/completions"); "none" removes the segment for hosts whose api-url already carries
    /// the full version path (e.g. Z.ai's https://api.z.ai/api/paas/v4/ → "chat/completions").
    /// </summary>
    [RConfigProperty("general_api-version-path")]
    public string? APIVersionPath { get; set; }
    
    // Model settings
    [RConfigProperty("general_default-model")]
    public string? DefaultModel { get; set; }
    
    [RConfigProperty("general_supports-prompt-completion")]
    public bool? SupportsCompletion { get; set; }

    /// <summary>
    /// Indicates whether the provider supports the Responses API for completions.
    /// If not specified, model-level settings or protocol defaults may apply.
    /// </summary>
    [RConfigProperty("general_supports-response-completion")]
    public bool? SupportsResponseCompletion { get; set; }

    /// <summary>
    /// Indicates whether models on this provider accept image inputs (vision / multimodal) by
    /// default. A model-level <c>supports-vision</c> overrides this. Consumed by the file-reading
    /// tools to select a vision-capable reader model.
    /// </summary>
    [RConfigProperty("general_supports-vision")]
    public bool? SupportsVision { get; set; }

    // Guidance settings
    [RConfigProperty(("guidance_supports-guidance"))]
    public bool? SupportsGuidance { get; set; }
    
    // The provider's default schema strategy used when a prompt defers (GuidanceSchema = Default).
    // This is GuidanceSchemaType (not GuidanceType) so it can express auto vs manual JSON/regex.
    [RConfigProperty(("guidance_default-guidance-type"))]
    public GuidanceSchemaType? DefaultGuidanceType { get; set; }
    
    [RConfigProperty(("_default-guidance-string"))]
    public string? DefaultGuidanceString { get; set; }

    /// <summary>
    /// How JSON guidance is sent for OpenAI-compatible providers: <c>json-schema</c> (default; strict
    /// <c>response_format: json_schema</c>) or <c>json-object</c> for hosts that only accept
    /// <c>response_format: json_object</c> (e.g. Z.ai/GLM) — the schema then travels as an extra
    /// system message. Ignored by protocols that do not use OpenAI-compatible JSON guidance.
    /// </summary>
    [RConfigProperty(("guidance_json-schema-mode"))]
    public JsonSchemaMode? JsonSchemaMode { get; set; }

    // Gateway routing
    /// <summary>Gets or sets the upstreams to try first, in priority order.</summary>
    [RConfigProperty("routing_upstream-order")]
    public List<string>? UpstreamOrder { get; set; }

    /// <summary>Gets or sets the only upstreams that may serve requests.</summary>
    [RConfigProperty("routing_allowed-upstreams")]
    public List<string>? AllowedUpstreams { get; set; }

    /// <summary>Gets or sets upstreams that must not serve requests.</summary>
    [RConfigProperty("routing_blocked-upstreams")]
    public List<string>? BlockedUpstreams { get; set; }

    /// <summary>Gets or sets whether the gateway may use fallback upstreams.</summary>
    [RConfigProperty("routing_allow-fallbacks")]
    public bool? AllowFallbacks { get; set; }

    /// <summary>Gets or sets the attribute used to order eligible upstreams.</summary>
    [RConfigProperty("routing_selection-strategy")]
    public RoutingSelectionStrategy? SelectionStrategy { get; set; }

    /// <summary>Gets or sets whether routed upstreams must support every request parameter.</summary>
    [RConfigProperty("routing_require-request-parameter-support")]
    public bool? RequireRequestParameterSupport { get; set; }

    /// <summary>Gets or sets the model quantization formats eligible to serve requests.</summary>
    [RConfigProperty("routing_allowed-quantizations")]
    public List<string>? AllowedQuantizations { get; set; }

    /// <summary>Gets or sets the maximum input-token price in dollars per million tokens.</summary>
    [RConfigProperty("routing_maximum-cost-per-million-input-tokens")]
    public decimal? MaximumRoutingCostPerMillionInputTokens { get; set; }

    /// <summary>Gets or sets the maximum output-token price in dollars per million tokens.</summary>
    [RConfigProperty("routing_maximum-cost-per-million-output-tokens")]
    public decimal? MaximumRoutingCostPerMillionOutputTokens { get; set; }

    /// <summary>Gets or sets the preferred minimum generation throughput in tokens per second.</summary>
    [RConfigProperty("routing_preferred-minimum-throughput-tokens-per-second")]
    public double? PreferredMinimumThroughputTokensPerSecond { get; set; }

    /// <summary>Gets or sets the preferred maximum observed latency in seconds.</summary>
    [RConfigProperty("routing_preferred-maximum-latency-seconds")]
    public double? PreferredMaximumLatencySeconds { get; set; }

    /// <summary>Gets or sets the required upstream prompt and response retention policy.</summary>
    [RConfigProperty("routing_data-retention")]
    public DataRetentionPolicy? DataRetention { get; set; }

    /// <summary>Gets or sets whether routed upstreams may collect request data.</summary>
    [RConfigProperty("routing_data-collection")]
    public DataCollectionPolicy? DataCollection { get; set; }

    /// <summary>Gets or sets the required geographic processing boundary.</summary>
    [RConfigProperty("routing_data-residency")]
    public DataResidencyPolicy? DataResidency { get; set; }

    // Rate Limiting
    [RConfigProperty("limiting_timeout-seconds")]
    public int? TimeoutSeconds { get; set; }
    
    [RConfigProperty("limiting_delay-between-requests-ms")]
    public int? DelayBetweenRequestsMs { get; set; }
    
    [RConfigProperty("limiting_retry-attempt-limit")]
    public int? RetryAttemptLimit { get; set; }
    
    [RConfigProperty("limiting_retry-initial-delay-seconds")]
    public int? RetryInitialDelaySeconds { get; set; }
    
    [RConfigProperty("limiting_simultaneous-requests")]
    public int? SimultaneousRequests { get; set; }
    
    
    // ==============
    //  Constructors
    // ==============
    
    // Called by "CallInitIfExists" in the "Read()" function of the RConfigParser class
    public void Init()
    {
        //Util.Log($"Init: SupportsGuidance {SupportsGuidance}");
        if (string.IsNullOrEmpty(APIURL))
            throw new Exception("Missing API URL!");
        
        // Resolve API key from environment if requested
        if (!string.IsNullOrEmpty(APIKey) && APIKey.Equals("environment", StringComparison.InvariantCultureIgnoreCase))
        {
            string providerName = (Name ?? string.Empty).Trim();
            // Construct environment variable name: PROVAPIKEY_<PROVIDERNAME>
            string envVarName = APIKeyEnvironment ?? ("PROVAPIKEY__" + providerName
                .Replace('-', '_')
                .Replace(' ', '_')
                .ToUpperInvariant());
            string? envApiKey = Environment.GetEnvironmentVariable(envVarName);
            if (string.IsNullOrWhiteSpace(envApiKey))
            {
                Util.Log($"Environment variable '{envVarName}' not found or empty for provider '{Name}'. Using empty API key.");
                APIKey = string.Empty;
            }
            else
            {
                APIKey = envApiKey;
            }
        }
        
        if (Protocol == global::Revi.Protocol.SystemOne)
        {
            InferenceClient = null;
            EmbeddingClient = null;
            if (DecisionClient is null)
            {
                SystemOneClient client = new(this);
                DecisionClient = client;
                _ownedDecisionClient = client;
            }
            return;
        }

        switch (Protocol)
        {
            case global::Revi.Protocol.OpenAI:
            case global::Revi.Protocol.OpenRouter:
                SupportsCompletion = false;
                break;
            
            case global::Revi.Protocol.vLLM:
                break;
            
            case global::Revi.Protocol.LLamaAPI:
                break;
            
            case global::Revi.Protocol.Claude:
                // Guidance is no longer forced off: Anthropic's structured outputs
                // (output_config.format json_schema) are emitted by TransformToClaudePayload.
                // The file's supports-guidance value now applies — set it false for providers
                // whose models predate structured-output support (pre-Haiku-4.5/Opus-4.5).
                SupportsCompletion = true;
                break;
            
            case global::Revi.Protocol.Gemini:
                //SupportsGuidance = true;
                //SupportsCompletion = true;
                break;
            
            default:
            //case ProviderType.Custom:
            {
                if (string.IsNullOrEmpty(APIURL))
                    throw new Exception("Protocol not set or missing API URL for custom API!");
                
                break;
            }
        }
        
        //Util.Log($"API Key: {APIKey}");
        InferenceClient = new InferClient(
            apiUrl: APIURL,
            apiKey: APIKey ?? "",
            protocol: Protocol ?? global::Revi.Protocol.vLLM,
            defaultModel: DefaultModel ?? "default",
            timeoutSeconds: TimeoutSeconds ?? 100,
            delayBetweenRequestsMs: DelayBetweenRequestsMs ?? 0,
            retryAttemptLimit: RetryAttemptLimit ?? 5,
            retryInitialDelaySeconds: RetryInitialDelaySeconds ?? 5,
            simultaneousRequests: SimultaneousRequests ?? 10,
            supportsCompletion: SupportsCompletion ?? false,
            supportsResponseCompletion: SupportsResponseCompletion ?? false,
            supportsGuidance: SupportsGuidance ?? false,
            // Reduce the schema strategy to the low-level decode mode for the client-level fallback.
            defaultGuidanceType: GuidanceResolver.ReduceToGuidanceType(DefaultGuidanceType),
            defaultGuidanceString: DefaultGuidanceString ?? "",
            jsonSchemaMode: JsonSchemaMode ?? global::Revi.JsonSchemaMode.JsonSchema,
            apiVersionPath: APIVersionPath,
            providerName: Name ?? string.Empty,
            routingPolicy: BuildRoutingPolicy());
        
        // Initialize EmbedClient for embeddings
        EmbeddingClient = new EmbedClient(
            apiUrl: APIURL,
            apiKey: APIKey ?? "",
            protocol: Protocol ?? global::Revi.Protocol.OpenAI,
            defaultModel: DefaultModel ?? "text-embedding-ada-002",
            timeoutSeconds: TimeoutSeconds ?? 100,
            delayBetweenRequestsMs: DelayBetweenRequestsMs ?? 0,
            retryAttemptLimit: RetryAttemptLimit ?? 5,
            retryInitialDelaySeconds: RetryInitialDelaySeconds ?? 5,
            simultaneousRequests: SimultaneousRequests ?? 10);
    }

    /// <summary>Builds the adapter-neutral routing policy from flattened RConfig properties.</summary>
    /// <returns>The routing policy passed to the inference client.</returns>
    private ProviderRoutingPolicy BuildRoutingPolicy()
    {
        return new ProviderRoutingPolicy
        {
            UpstreamOrder = UpstreamOrder,
            AllowedUpstreams = AllowedUpstreams,
            BlockedUpstreams = BlockedUpstreams,
            AllowFallbacks = AllowFallbacks,
            SelectionStrategy = SelectionStrategy,
            RequireRequestParameterSupport = RequireRequestParameterSupport,
            AllowedQuantizations = AllowedQuantizations,
            MaximumCostPerMillionInputTokens = MaximumRoutingCostPerMillionInputTokens,
            MaximumCostPerMillionOutputTokens = MaximumRoutingCostPerMillionOutputTokens,
            PreferredMinimumThroughputTokensPerSecond = PreferredMinimumThroughputTokensPerSecond,
            PreferredMaximumLatencySeconds = PreferredMaximumLatencySeconds,
            DataRetention = DataRetention,
            DataCollection = DataCollection,
            DataResidency = DataResidency
        };
    }

    // Empty constructor for the serialization function
    public ProviderProfile() {}
    
    // Normal constructor
    public ProviderProfile(
        string name,
        bool enabled = true,
        global::Revi.Protocol protocol = global::Revi.Protocol.OpenAI,
        string apiURL = "", 
        string apiKey = "",
        int timeoutSeconds = 100,
        int delayBetweenRequestsMs = 0,
        int retryAttemptLimit = 5,
        int retryInitialDelaySeconds = 5,
        int simultaneousRequests = 10,
        string defaultModel = "default",
        bool supportsCompletion = false,
        bool supportsResponseCompletion = false,
        bool supportsVision = false,
        bool supportsGuidance = false,
        GuidanceSchemaType? defaultGuidanceType = null,
        string? defaultGuidanceString = null)
    {
        Name = name;
        Enabled = enabled;

        Protocol = protocol;

        APIURL = apiURL;
        APIKey = apiKey;
        
        TimeoutSeconds = timeoutSeconds;
        DelayBetweenRequestsMs = delayBetweenRequestsMs;
        RetryAttemptLimit = retryAttemptLimit;
        RetryInitialDelaySeconds = retryInitialDelaySeconds;
        SimultaneousRequests = simultaneousRequests;

        DefaultModel = defaultModel;
        SupportsCompletion = supportsCompletion;
        SupportsResponseCompletion = supportsResponseCompletion;
        SupportsVision = supportsVision;

        SupportsGuidance = supportsGuidance;
        DefaultGuidanceType = defaultGuidanceType;
        DefaultGuidanceString = defaultGuidanceString;
        
        // Validate that there is not another ProviderProfile with the same Protocol, API URL, and API key
        
        Init();
    }
}
