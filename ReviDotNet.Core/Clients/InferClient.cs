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

namespace Revi;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

/// <summary>
/// Provides asynchronous client functionality for interacting with an AI inference service.
/// </summary>
public class InferClient : IDisposable
{
    // ==============
    //  Declarations
    // ==============
    
    #region Declarations
    private readonly HttpClient _httpClient;
    private readonly InferClientConfig _config;
    private readonly SemaphoreSlim _clientSemaphore;
    private readonly RateLimiter _rateLimiter;
    private readonly InferenceHttpClient _inferenceHttpClient;
    private readonly StreamingProcessor _streamingProcessor;
    private readonly PayloadTransformer _payloadTransformer;
    #endregion
    

    // =============
    //  Constructor
    // =============
    
    #region Constructor
    /// <summary>
    /// Initializes a new instance of the AsyncInferenceClient class that handles requests to an AI model inference API.
    /// </summary>
    /// <param name="apiUrl">The base URL of the AI inference API.</param>
    /// <param name="apiKey">The API key used for authenticating with the inference API. If not specified, authentication is disabled.</param>
    /// <param name="defaultModel">The identifier for the default model used for inference requests. Default is "mistralai/Mistral-7B-Instruct-v0.1".</param>
    /// <param name="timeoutSeconds">The timeout in seconds for HTTP requests to the API. Default is 100 seconds.</param>
    /// <param name="delayBetweenRequestsMs">The minimum delay in milliseconds between subsequent requests to the API to avoid rate limits. Default is 0 ms.</param>
    /// <param name="retryAttemptLimit">The maximum number of retry attempts for a request in case of failures. Default is 5 attempts.</param>
    /// <param name="retryInitialDelaySeconds">The initial delay in seconds before the first retry attempt. Default is 5 seconds.</param>
    /// <param name="simultaneousRequests">The maximum number of simultaneous requests allowed to the API. Default is 10.</param>
    /// <param name="supportsCompletion">Indicates whether the client supports prompt completion instead of just chat completion. Default is false.</param>
    public InferClient(
        string apiUrl,
        string apiKey = "",
        Protocol protocol = Protocol.vLLM,
        string defaultModel = "mistralai/Mistral-7B-Instruct-v0.1",
        int timeoutSeconds = 100,
        int delayBetweenRequestsMs = 0,
        int retryAttemptLimit = 5,
        int retryInitialDelaySeconds = 5,
        int simultaneousRequests = 10,
        bool supportsCompletion = false,
        bool supportsGuidance = false,
        GuidanceType? defaultGuidanceType = GuidanceType.Disabled,
        string? defaultGuidanceString = null,
        HttpClient? httpClientOverride = null)
    {
        // Create configuration
        _config = new InferClientConfig
        {
            ApiUrl = apiUrl,
            ApiKey = apiKey,
            UseApiKey = !string.IsNullOrEmpty(apiKey),
            Protocol = protocol,
            DefaultModel = defaultModel,
            TimeoutSeconds = timeoutSeconds,
            DelayBetweenRequestsMs = delayBetweenRequestsMs,
            RetryAttemptLimit = retryAttemptLimit,
            RetryInitialDelaySeconds = retryInitialDelaySeconds,
            SimultaneousRequests = simultaneousRequests,
            SupportsCompletion = supportsCompletion,
            SupportsGuidance = supportsGuidance,
            DefaultGuidanceType = defaultGuidanceType,
            DefaultGuidanceString = defaultGuidanceString
        };
        
        // Create shared resources
        _clientSemaphore = new SemaphoreSlim(simultaneousRequests);

        // HTTP Client Setup
        apiUrl = apiUrl.TrimEnd('/') + "/";
        
        if (apiUrl.EndsWith("v1/chat/completions"))
            throw new Exception("Please remove v1/chat/completions from the end of API URL");
        
        if (apiUrl.EndsWith("v1/completions"))
            throw new Exception("Please remove v1/completions from the end of API URL"); 
        
        // Use provided HttpClient if available (for testing), otherwise create a new one
        _httpClient = httpClientOverride ?? new HttpClient { BaseAddress = new Uri(apiUrl) };
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(apiUrl);
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Accept"))
        {
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }
        //_client.DefaultRequestHeaders.Add("Content-Type", "application/json");
        if (_config.UseApiKey)
        {
            if (_config.Protocol == Protocol.Gemini)
            {
                // Gemini uses query parameter for API key instead of Bearer token
                // API key will be added to the URL in the request
            }
            else if (_config.Protocol == Protocol.Claude)
            {
                // Anthropic uses x-api-key header and requires anthropic-version
                if (!_httpClient.DefaultRequestHeaders.Contains("x-api-key"))
                    _httpClient.DefaultRequestHeaders.Add("x-api-key", _config.ApiKey);
                if (!_httpClient.DefaultRequestHeaders.Contains("anthropic-version"))
                    _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }
            else if (!_httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.ApiKey}");
            }
        }
        _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        
        // Initialize components with shared dependencies
        _rateLimiter = new RateLimiter(delayBetweenRequestsMs);
        _payloadTransformer = new PayloadTransformer(_config);
        
        _inferenceHttpClient = new InferenceHttpClient(
            _config, 
            _clientSemaphore, 
            _rateLimiter, 
            _payloadTransformer, 
            _httpClient);
        
        _streamingProcessor = new StreamingProcessor(
            _config, 
            _clientSemaphore, 
            _rateLimiter,
            _payloadTransformer,
            _httpClient);
        
    }
    #endregion
    
    
    // ==========
    //  Disposal
    // ==========
    
    #region Disposal
    /// <summary>
    /// Disposes the HttpClient instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
        _inferenceHttpClient?.Dispose();
        _streamingProcessor?.Dispose();
        _clientSemaphore?.Dispose();
        _rateLimiter?.Dispose();
    }
    #endregion

    
    // ===================
    //  Prompt Completion
    // ===================
    
    #region Prompt Completion

    /// <summary>
    /// Generates a text completion based on the provided prompt and optional parameters using the specified AI model.
    /// </summary>
    /// <param name="prompt">The input text that serves as the prompt for the completion generation.</param>
    /// <param name="model">The model to be used for generating the completion. If set to "default", the default model configured for the client is used.</param>
    /// <param name="temperature">Controls the randomness of the output. Higher values generate more creative responses, while lower values yield more focused responses. Optional.</param>
    /// <param name="topP">Limits the sampling to a subset of the most likely tokens where the sum of their probabilities is greater than the specified threshold. Optional.</param>
    /// <param name="topK">Limits the sampling to the top K most probable tokens. Optional.</param>
    /// <param name="bestOf">Generates multiple completions server-side and returns the one with the highest log-probability per token. Optional.</param>
    /// <param name="maxTokens">Specifies the maximum number of tokens to generate in the completion. Optional.</param>
    /// <param name="frequencyPenalty">Applies a penalty to discourage repeated phrases or tokens based on their frequency in the completion. Optional.</param>
    /// <param name="presencePenalty">Applies a penalty to encourage the inclusion of new tokens not already in the text. Optional.</param>
    /// <param name="stopSequences">An array of strings where the completion generation stops if any of the specified sequences is encountered. Optional.</param>
    /// <param name="guidanceType">Specifies the type of guidance to apply during completion generation, if applicable. Default is Disabled.</param>
    /// <param name="guidanceString">Specifies a guidance configuration string, if applicable, for AI-generated results. Optional.</param>
    /// <param name="cancellationToken">Token to observe cancellation requests, allowing the operation to be aborted. Default is none.</param>
    /// <returns>Returns a <see cref="CompletionResponse"/> containing the generated text completion and related information.</returns>
    /// <exception cref="Exception">Throws an exception if the client does not support prompt completion.</exception>
    public async Task<CompletionResponse> GenerateAsync(
        string prompt,
        string model = "default",
        float? temperature = null,
        int? topK = null,
        float? topP = null,
        float? minP = null,
        int? bestOf = null,
        MaxTokenType? maxTokenType = null,
        int? maxTokens = null,
        float? frequencyPenalty = null,
        float? presencePenalty = null,
        float? repetitionPenalty = null,
        string[]? stopSequences = null,
        GuidanceType? guidanceType = GuidanceType.Disabled,
        string? guidanceString = null,
        bool? useSearchGrounding = null,
        CancellationToken cancellationToken = default,
        int? inactivityTimeoutSeconds = null)
    {
        if (_config.SupportsCompletion is false)
            throw new Exception("Attempting prompt completion on provider that does not support it");

        model = model == "default" ? _config.DefaultModel : model;
        Dictionary<string, object> parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "prompt", prompt }
        };

        _payloadTransformer.AddOptionalParameters(
            parameters,
            temperature,
            topK,
            topP,
            minP,
            bestOf,
            maxTokenType,
            maxTokens,
            frequencyPenalty,
            presencePenalty,
            repetitionPenalty,
            stopSequences,
            guidanceType,
            guidanceString,
            useSearchGrounding);
        
        // Use appropriate endpoint based on protocol
        string endpoint;
        if (_config.Protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:generateContent";
        else if (_config.Protocol == Protocol.Claude)
            endpoint = "v1/messages";
        else
            endpoint = "v1/completions";

        CompletionResponse response;
        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";
        
        try
        {
            Dictionary<string, string> serverResponse = await _inferenceHttpClient.ExecuteRequest(endpoint, parameters, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds);
            response = BuildResponse(prompt, serverResponse);
        }

        // Dump error message if an exception occurred
        catch (Exception e)
        {
            string errorMessage = $"### ReviDotNet.GenerateAsync() Error Generating Completion\n" +
                                 $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                 $"# Payload\n{payloadDebug}\n\n" +
                                 $"# Exception\n{e.Message}\n\n";
            Util.Log(errorMessage);
            await Util.DumpLog(errorMessage, "ic-generate-prompt-error");
            throw;
        }
        
        // Dump successful logging if successful
        string responseDebug = $"'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''";
        string dumpMessage = $"### ReviDotNet.GenerateAsync() Prompt Completion\n" +
                             $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                             $"# Payload\n{payloadDebug}\n\n" +
                             $"# Response\n{responseDebug}\n\n";
        await Util.DumpLog(dumpMessage, "ic-generate-prompt");
        
        return response;
    }
    
    /// <summary>
    /// This method processes the server response and creates a Response object.
    /// </summary>
    /// <param name="prompt">The prompt used as input to the inference.</param>
    /// <param name="serverResponse">The response received from the server.</param>
    /// <returns>A CompletionResponse object containing the processed response.</returns>
    private CompletionResponse BuildResponse(string prompt, Dictionary<string, string> serverResponse)
    {
        // This method processes the server response and creates a Response object
        List<string> outputs = new List<string>();
        string selected = serverResponse.GetValueOrDefault("text", "");
        string finishReason = serverResponse.GetValueOrDefault("finish_reason", "");

        outputs.Add(selected); // Simulating multiple outputs; adjust based on actual API capabilities

        return new CompletionResponse { FullPrompt = prompt, Outputs = outputs, Selected = selected, FinishReason = finishReason };
    }
    #endregion
    
    
    // =================
    //  Chat Completion
    // =================
    
    #region Chat Completion

    /// <summary>
    /// Generates a chat completion response asynchronously based on the provided input messages and optional configuration parameters.
    /// </summary>
    /// <param name="messages">A list of messages constituting the conversation input for the AI model.</param>
    /// <param name="model">The identifier of the AI model to use. If not specified, the default model is used.</param>
    /// <param name="temperature">The sampling temperature parameter that affects randomness in the response. Higher values yield more random outputs.</param>
    /// <param name="topP">The top-p sampling parameter that limits the response to the smallest set of tokens with a cumulative probability ≥ topP.</param>
    /// <param name="topK">The top-k sampling parameter that limits the response to the k most probable next tokens.</param>
    /// <param name="bestOf">The number of best completions to consider. The highest-ranking result is returned.</param>
    /// <param name="maxTokens">The maximum number of tokens allowed in the response.</param>
    /// <param name="frequencyPenalty">The penalty applied to reduce the likelihood of token repetition.</param>
    /// <param name="presencePenalty">The penalty applied to encourage the inclusion of new tokens in the response.</param>
    /// <param name="stopSequences">An array of strings that, if generated, will halt further output generation.</param>
    /// <param name="guidanceType">The type of guidance to apply when generating responses. Default is <see cref="GuidanceType.Disabled"/>.</param>
    /// <param name="guidanceString">Additional guidance instructions for customizing the behavior of the AI model during generation.</param>
    /// <param name="cancellationToken">A cancellation token to observe for cancellation requests during execution.</param>
    /// <returns>A task that represents the asynchronous operation and resolves with a <see cref="CompletionResponse"/> containing the generated output.</returns>
    public async Task<CompletionResponse> GenerateAsync(
        List<Message> messages,
        string model = "default",
        float? temperature = null,
        int? topK = null,
        float? topP = null,
        float? minP = null,
        int? bestOf = null,
        MaxTokenType? maxTokenType = null,
        int? maxTokens = null,
        float? frequencyPenalty = null,
        float? presencePenalty = null,
        float? repetitionPenalty = null,
        string[]? stopSequences = null,
        GuidanceType? guidanceType = GuidanceType.Disabled,
        string? guidanceString = null,
        bool? useSearchGrounding = null,
        CancellationToken cancellationToken = default,
        int? inactivityTimeoutSeconds = null)
    {
        model = model == "default" ? _config.DefaultModel : model;
        Dictionary<string, object> parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "messages", messages }
        };

        // Add optional parameters if they are not null
        _payloadTransformer.AddOptionalParameters(
            parameters,
            temperature,
            topK,
            topP,
            minP,
            bestOf,
            maxTokenType,
            maxTokens,
            frequencyPenalty,
            presencePenalty,
            repetitionPenalty,
            stopSequences,
            guidanceType,
            guidanceString,
            useSearchGrounding);

        string endpoint;
        if (_config.Protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:generateContent";
        else if (_config.Protocol == Protocol.Claude)
        {
            endpoint = "v1/messages";
        }
        else
        {
            endpoint = "v1/chat/completions";
        }

        CompletionResponse response;
        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";

        try
        {
            Dictionary<string, string> serverResponse = await _inferenceHttpClient.ExecuteRequest(endpoint, parameters, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds);
            response = BuildResponse(messages, serverResponse);
        }
        
        // Dump error message if exception occurred
        catch (Exception e)
        {
            string errorMessage = $"### ReviDotNet.GenerateAsync() Error Generating Chat Completion\n" +
                                  $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                  $"# Payload\n{payloadDebug}\n\n" +
                                  $"# Exception\n{e.Message}\n\n";
            Util.Log(errorMessage);
            await Util.DumpLog(errorMessage, "ic-generate-chat-error");
            throw;
        }

        // Dump successful logging if successful
        string responseDebug = $"'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''";
        string dumpMessage = $"### ReviDotNet.GenerateAsync() Chat Completion\n" +
                             $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                             $"# Payload\n{payloadDebug}\n\n" +
                             $"# Response\n{responseDebug}\n\n";
        await Util.DumpLog(dumpMessage, "ic-generate-chat");
        
        return response;
    }
    
    /// <summary>
    /// This method processes the server response and creates a Response object.
    /// </summary>
    /// <param name="messages">The list of messages.</param>
    /// <param name="serverResponse">The response received from the server.</param>
    /// <returns>A Response object containing the processed response.</returns>
    private CompletionResponse BuildResponse(List<Message> messages, Dictionary<string, string> serverResponse)
    {
        string fullPrompt = JsonConvert.SerializeObject(messages, Formatting.Indented);
        return BuildResponse(fullPrompt, serverResponse);
    }
    #endregion


    // ==================
    //  Prompt Streaming
    // ==================
    
    #region Prompt Streaming
    /// <summary>
    /// Generates a streaming text completion based on the provided prompt and optional parameters using the specified AI model.
    /// </summary>
    /// <param name="prompt">The input text that serves as the prompt for the completion generation.</param>
    /// <param name="model">The model to be used for generating the completion. If set to "default", the default model configured for the client is used.</param>
    /// <param name="temperature">Controls the randomness of the output. Higher values generate more creative responses, while lower values yield more focused responses. Optional.</param>
    /// <param name="topP">Limits the sampling to a subset of the most likely tokens where the sum of their probabilities is greater than the specified threshold. Optional.</param>
    /// <param name="topK">Limits the sampling to the top K most probable tokens. Optional.</param>
    /// <param name="bestOf">Generates multiple completions server-side and returns the one with the highest log-probability per token. Optional.</param>
    /// <param name="maxTokens">Specifies the maximum number of tokens to generate in the completion. Optional.</param>
    /// <param name="frequencyPenalty">Applies a penalty to discourage repeated phrases or tokens based on their frequency in the completion. Optional.</param>
    /// <param name="presencePenalty">Applies a penalty to encourage the inclusion of new tokens not already in the text. Optional.</param>
    /// <param name="stopSequences">An array of strings where the completion generation stops if any of the specified sequences is encountered. Optional.</param>
    /// <param name="guidanceType">Specifies the type of guidance to apply during completion generation, if applicable. Default is Disabled.</param>
    /// <param name="guidanceString">Specifies a guidance configuration string, if applicable, for AI-generated results. Optional.</param>
    /// <param name="cancellationToken">Token to observe cancellation requests, allowing the operation to be aborted. Default is none.</param>
    /// <returns>Returns an async enumerable that yields streaming text chunks as they are generated.</returns>
    /// <exception cref="Exception">Throws an exception if the client does not support prompt completion.</exception>
    public StreamingResult<string> GenerateStreamAsync(
        string prompt,
        string model = "default",
        float? temperature = null,
        int? topK = null,
        float? topP = null,
        float? minP = null,
        int? bestOf = null,
        MaxTokenType? maxTokenType = null,
        int? maxTokens = null,
        float? frequencyPenalty = null,
        float? presencePenalty = null,
        float? repetitionPenalty = null,
        string[]? stopSequences = null,
        GuidanceType? guidanceType = GuidanceType.Disabled,
        string? guidanceString = null,
        bool? useSearchGrounding = null,
        CancellationToken cancellationToken = default,
        int? inactivityTimeoutSeconds = null)
    {
        if (_config.SupportsCompletion is false)
        {
            string errorMessage = $"### ReviDotNet.GenerateStreamAsync() Error - Prompt Streaming Not Supported\n" +
                                 $"# Error\nAttempting prompt completion on provider that does not support it\n\n";
            Util.Log(errorMessage);
            Task.Run(async () => await Util.DumpLog(errorMessage, "ic-stream-prompt-error"));
            throw new Exception("Attempting prompt completion on provider that does not support it");
        }

        model = model == "default" ? _config.DefaultModel : model;
        var parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "prompt", prompt },
            { "stream", true }
        };

        _payloadTransformer.AddOptionalParameters(
            parameters,
            temperature,
            topK,
            topP,
            minP,
            bestOf,
            maxTokenType,
            maxTokens,
            frequencyPenalty,
            presencePenalty,
            repetitionPenalty,
            stopSequences,
            guidanceType,
            guidanceString,
            useSearchGrounding);
        
        // Use appropriate endpoint based on protocol
        string endpoint;
        if (_config.Protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:streamGenerateContent?alt=sse";
        else if (_config.Protocol == Protocol.Claude)
            endpoint = "v1/messages";
        else
            endpoint = "v1/completions";

        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";
        
        // Log the start of streaming request
        /*string startMessage = $"### ReviDotNet.GenerateStreamAsync() Prompt Streaming - Started\n" +
                             $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                             $"# Payload\n{payloadDebug}\n\n";
        Task.Run(async () => await Util.DumpLog(startMessage, "ic-stream-prompt-start"));*/
        
        try
        {
            IAsyncEnumerable<string> streamingEnumerable = _streamingProcessor.ExecuteStreamingRequest(endpoint, parameters, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds);
            return _streamingProcessor.CreateStreamingResultWrapper(streamingEnumerable, cancellationToken, 
                async (completion, fullResponse) => {
                    // This callback will be called when streaming completes with the full response
                    string completionMessage = $"### ReviDotNet.GenerateStreamAsync() Prompt Streaming - Completed\n" +
                                             $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                             $"# Payload\n{payloadDebug}\n\n" +
                                             $"# Response\n'''\n{fullResponse}\n'''\n\n" +
                                             $"# Completion Info\n{JsonConvert.SerializeObject(completion, Formatting.Indented)}\n\n";
                    await Util.DumpLog(completionMessage, "ic-stream-prompt");
                },
                async (ex) => {
                    // This callback will be called if streaming fails
                    string errorMessage = $"### ReviDotNet.GenerateStreamAsync() Prompt Streaming - Error\n" +
                                         $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                         $"# Payload\n{payloadDebug}\n\n" +
                                         $"# Exception\n{ex.Message}\n\n";
                    await Util.DumpLog(errorMessage, "ic-stream-prompt-error");
                });
        }
        catch (Exception e)
        {
            string errorMessage = $"### ReviDotNet.GenerateStreamAsync() Prompt Streaming - Setup Error\n" +
                                 $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                 $"# Payload\n{payloadDebug}\n\n" +
                                 $"# Exception\n{e.Message}\n\n";
            Util.Log(errorMessage);
            Task.Run(async () => await Util.DumpLog(errorMessage, "ic-stream-prompt-error"));
            throw;
        }
    }
    #endregion
    
    
    // ================
    //  Chat Streaming
    // ================
    
    #region Chat Streaming
    /// <summary>
    /// Generates a streaming chat completion response asynchronously based on the provided input messages and optional configuration parameters.
    /// </summary>
    /// <param name="messages">A list of messages constituting the conversation input for the AI model.</param>
    /// <param name="model">The identifier of the AI model to use. If not specified, the default model is used.</param>
    /// <param name="temperature">The sampling temperature parameter that affects randomness in the response. Higher values yield more random outputs.</param>
    /// <param name="topP">The top-p sampling parameter that limits the response to the smallest set of tokens with a cumulative probability ≥ topP.</param>
    /// <param name="topK">The top-k sampling parameter that limits the response to the k most probable next tokens.</param>
    /// <param name="bestOf">The number of best completions to consider. The highest-ranking result is returned.</param>
    /// <param name="maxTokens">The maximum number of tokens allowed in the response.</param>
    /// <param name="frequencyPenalty">The penalty applied to reduce the likelihood of token repetition.</param>
    /// <param name="presencePenalty">The penalty applied to encourage the inclusion of new tokens in the response.</param>
    /// <param name="stopSequences">An array of strings that, if generated, will halt further output generation.</param>
    /// <param name="guidanceType">The type of guidance to apply when generating responses. Default is <see cref="GuidanceType.Disabled"/>.</param>
    /// <param name="guidanceString">Additional guidance instructions for customizing the behavior of the AI model during generation.</param>
    /// <param name="cancellationToken">A cancellation token to observe for cancellation requests during execution.</param>
    /// <returns>An async enumerable that yields streaming text chunks as they are generated.</returns>
    public StreamingResult<string> GenerateStreamAsync(
        List<Message> messages,
        string model = "default",
        float? temperature = null,
        int? topK = null,
        float? topP = null,
        float? minP = null,
        int? bestOf = null,
        MaxTokenType? maxTokenType = null,
        int? maxTokens = null,
        float? frequencyPenalty = null,
        float? presencePenalty = null,
        float? repetitionPenalty = null,
        string[]? stopSequences = null,
        GuidanceType? guidanceType = GuidanceType.Disabled,
        string? guidanceString = null,
        bool? useSearchGrounding = null,
        CancellationToken cancellationToken = default,
        int? inactivityTimeoutSeconds = null)
    {
        model = model == "default" ? _config.DefaultModel : model;
        var parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "messages", messages },
            { "stream", true }
        };

        _payloadTransformer.AddOptionalParameters(
            parameters,
            temperature,
            topK,
            topP,
            minP,
            bestOf,
            maxTokenType,
            maxTokens,
            frequencyPenalty,
            presencePenalty,
            repetitionPenalty,
            stopSequences,
            guidanceType,
            guidanceString,
            useSearchGrounding);

        string endpoint;
        if (_config.Protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:streamGenerateContent?alt=sse";
        else
            endpoint = "v1/chat/completions";

        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";
        
        // Log the start of streaming request
        /*string startMessage = $"### ReviDotNet.GenerateStreamAsync() Chat Streaming - Started\n" +
                             $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                             $"# Payload\n{payloadDebug}\n\n";
        Task.Run(async () => await Util.DumpLog(startMessage, "ic-stream-chat-start"));*/

        try
        {
            var streamingEnumerable = _streamingProcessor.ExecuteStreamingRequest(endpoint, parameters, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds);
            return _streamingProcessor.CreateStreamingResultWrapper(streamingEnumerable, cancellationToken,
                async (completion, fullResponse) => {
                    // This callback will be called when streaming completes with the full response
                    string completionMessage = $"### ReviDotNet.GenerateStreamAsync() Chat Streaming - Completed\n" +
                                             $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                             $"# Payload\n{payloadDebug}\n\n" +
                                             $"# Response\n'''\n{fullResponse}\n'''\n\n" +
                                             $"# Completion Info\n{JsonConvert.SerializeObject(completion, Formatting.Indented)}\n\n";
                    await Util.DumpLog(completionMessage, "ic-stream-chat");
                },
                async (ex) => {
                    // This callback will be called if streaming fails
                    string errorMessage = $"### ReviDotNet.GenerateStreamAsync() Chat Streaming - Error\n" +
                                         $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                         $"# Payload\n{payloadDebug}\n\n" +
                                         $"# Exception\n{ex.Message}\n\n";
                    await Util.DumpLog(errorMessage, "ic-stream-chat-error");
                });
        }
        catch (Exception ex)
        {
            string errorMessage = $"### ReviDotNet.GenerateStreamAsync() Chat Streaming - Setup Error\n" +
                                 $"# URL\n{_httpClient.BaseAddress + endpoint}\n\n" +
                                 $"# Payload\n{payloadDebug}\n\n" +
                                 $"# Exception\n{ex.Message}\n\n";
            Util.Log(errorMessage);
            Task.Run(async () => await Util.DumpLog(errorMessage, "ic-stream-chat-error"));
            throw;
        }
    }
    #endregion
}