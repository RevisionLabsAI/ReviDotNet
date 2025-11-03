// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

using System.Runtime.InteropServices.ObjectiveC;
using Newtonsoft.Json;
using Revi;

// ReSharper disable InconsistentNaming

namespace Revi;

public class ProviderProfile
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
    
    [RConfigProperty("general_protocol")]
    public Protocol? Protocol { get; set; }
    
    // API Settings
    [RConfigProperty("general_api-url")]
    public string? APIURL { get; set; }
    
    [RConfigProperty("general_api-key")]
    public string? APIKey { get; set; }
    
    // Model settings
    [RConfigProperty("general_default-model")]
    public string? DefaultModel { get; set; }
    
    [RConfigProperty("general_supports-prompt-completion")]
    public bool? SupportsCompletion { get; set; }

    // Guidance settings
    [RConfigProperty(("guidance_supports-guidance"))]
    public bool? SupportsGuidance { get; set; }
    
    [RConfigProperty(("guidance_default-guidance-type"))]
    public GuidanceType? DefaultGuidanceType { get; set; }
    
    [RConfigProperty(("_default-guidance-string"))]
    public string? DefaultGuidanceString { get; set; }

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
            string envVarName = "PROVAPIKEY__" + providerName
                .Replace('-', '_')
                .Replace(' ', '_')
                .ToUpperInvariant();
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
        
        switch (Protocol)
        {
            case Revi.Protocol.OpenAI: 
                SupportsCompletion = false;
                break;
            
            case Revi.Protocol.vLLM:
                break;
            
            case Revi.Protocol.LLamaAPI:
                break;
            
            case Revi.Protocol.Claude:
                SupportsGuidance = false;
                SupportsCompletion = true;
                break;
            
            case Revi.Protocol.Gemini:
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
            protocol: Protocol ?? Revi.Protocol.vLLM,
            defaultModel: DefaultModel ?? "default",
            timeoutSeconds: TimeoutSeconds ?? 100,
            delayBetweenRequestsMs: DelayBetweenRequestsMs ?? 0,
            retryAttemptLimit: RetryAttemptLimit ?? 5,
            retryInitialDelaySeconds: RetryInitialDelaySeconds ?? 5,
            simultaneousRequests: SimultaneousRequests ?? 10,
            supportsCompletion: SupportsCompletion ?? false,
            supportsGuidance: SupportsGuidance ?? false,
            defaultGuidanceType: DefaultGuidanceType ?? null,
            defaultGuidanceString: DefaultGuidanceString ?? "");
        
        // Initialize EmbedClient for embeddings
        EmbeddingClient = new EmbedClient(
            apiUrl: APIURL,
            apiKey: APIKey ?? "",
            protocol: Protocol ?? Revi.Protocol.OpenAI,
            defaultModel: DefaultModel ?? "text-embedding-ada-002",
            timeoutSeconds: TimeoutSeconds ?? 100,
            delayBetweenRequestsMs: DelayBetweenRequestsMs ?? 0,
            retryAttemptLimit: RetryAttemptLimit ?? 5,
            retryInitialDelaySeconds: RetryInitialDelaySeconds ?? 5,
            simultaneousRequests: SimultaneousRequests ?? 10);
    }

    // Empty constructor for the serialization function
    public ProviderProfile() {}
    
    // Normal constructor
    public ProviderProfile(
        string name,
        bool enabled = true,
        Protocol protocol = Revi.Protocol.OpenAI, 
        string apiURL = "", 
        string apiKey = "",
        int timeoutSeconds = 100,
        int delayBetweenRequestsMs = 0,
        int retryAttemptLimit = 5,
        int retryInitialDelaySeconds = 5,
        int simultaneousRequests = 10,
        string defaultModel = "default",
        bool supportsCompletion = false,
        bool supportsGuidance = false,
        GuidanceType? defaultGuidanceType = null,
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

        SupportsGuidance = supportsGuidance;
        DefaultGuidanceType = defaultGuidanceType;
        DefaultGuidanceString = defaultGuidanceString;
        
        // Validate that there is not another ProviderProfile with the same Protocol, API URL, and API key
        
        Init();
    }
}