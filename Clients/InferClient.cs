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
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices; // Add this for EnumeratorCancellation
using System.IO; // Add this for StreamReader
using System.Linq; // Add this for LINQ operations
using Newtonsoft.Json;

/// <summary>
/// Provides asynchronous client functionality for interacting with an AI inference service.
/// </summary>
public class AsyncInferenceClient : IDisposable
{
    // ==============
    //  Declarations
    // ==============
    
    #region Declarations
    private readonly HttpClient _client;
    private readonly string _defaultModel;
    private readonly string _apiKey;
    private readonly bool _useApiKey;
    private readonly Protocol _protocol;
    
    private readonly SemaphoreSlim _clientSemaphore;
    private static DateTime _lastExecutionTime;

    private readonly int _delayBetweenRequestsMs;
    private readonly int _retryAttemptLimit;
    private readonly int _retryInitialDelaySeconds;

    private readonly bool _supportsCompletion;
    
    private readonly bool _supportsGuidance;
    private readonly GuidanceType? _defaultGuidanceType;
    private readonly string? _defaultGuidanceString;
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
    public AsyncInferenceClient(
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
        string? defaultGuidanceString = null)
    {
        // Rate Limiting
        _delayBetweenRequestsMs = delayBetweenRequestsMs;
        _retryAttemptLimit = retryAttemptLimit;
        _retryInitialDelaySeconds = retryInitialDelaySeconds;

        _lastExecutionTime = DateTime.MinValue;
        _clientSemaphore = new SemaphoreSlim(simultaneousRequests);
        
        // API Key
        _apiKey = apiKey;
        _useApiKey = !string.IsNullOrEmpty(apiKey);
        
        // Model 
        _defaultModel = defaultModel;
        _supportsCompletion = supportsCompletion;
        
        // Guidance
        _supportsGuidance = supportsGuidance;
        _defaultGuidanceType = defaultGuidanceType;
        _defaultGuidanceString = defaultGuidanceString;
        
        
        // HTTP Client Setup
        _protocol = protocol;
        apiUrl = apiUrl.TrimEnd('/') + "/";
        
        if (apiUrl.EndsWith("v1/chat/completions"))
            throw new Exception("Please remove v1/chat/completions from the end of API URL");
        
        if (apiUrl.EndsWith("v1/completions"))
            throw new Exception("Please remove v1/completions from the end of API URL"); 
        
        _client = new HttpClient { BaseAddress = new Uri(apiUrl) };
        _client.DefaultRequestHeaders.Add("Accept", "application/json");
        //_client.DefaultRequestHeaders.Add("Content-Type", "application/json");
        
        if (_useApiKey)
        {
            if (_protocol == Protocol.Gemini)
            {
                // Gemini uses query parameter for API key instead of Bearer token
                // API key will be added to the URL in the request
            }
            else
            {
                _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            }
        }
        
        _client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
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
        CancellationToken cancellationToken = default)
    {
        if (_supportsCompletion is false)
            throw new Exception("Attempting prompt completion on provider that does not support it");

        model = model == "default" ? _defaultModel : model;
        var parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "prompt", prompt }
        };

        AddOptionalParameters(
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
            guidanceString);
        
        // Use appropriate endpoint based on protocol
        string endpoint;
        if (_protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:generateContent";
        else
            endpoint = "v1/completions";

        CompletionResponse response;
        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";
        
        try
        {
            Dictionary<string, string> serverResponse = await ExecuteRequest(endpoint, parameters, cancellationToken);
            response = BuildResponse(prompt, serverResponse);
        }

        // Dump error message if an exception occurred
        catch (Exception e)
        {
            string errorMessage = $"### ReviDotNet.GenerateAsync() Error Generating Completion\n" +
                                 $"# URL\n{_client.BaseAddress + endpoint}\n\n" +
                                 $"# Payload\n{payloadDebug}\n\n" +
                                 $"# Exception\n{e.Message}\n\n";
            Util.Log(errorMessage);
            await Util.DumpLog(errorMessage, "ic-generate-prompt-error");
            throw;
        }
        
        // Dump successful logging if successful
        string responseDebug = $"'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''";
        string dumpMessage = $"### ReviDotNet.GenerateAsync() Prompt Completion\n" +
                             $"# URL\n{_client.BaseAddress + endpoint}\n\n" +
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
        var outputs = new List<string>();
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
        CancellationToken cancellationToken = default)
    {
        model = model == "default" ? _defaultModel : model;
        var parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "messages", messages }
        };

        // Add optional parameters if they are not null
        AddOptionalParameters(
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
            guidanceString);

        string endpoint;
        if (_protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:generateContent";
        else
        {
            endpoint = "v1/chat/completions";
        }

        CompletionResponse response;
        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";

        try
        {
            Dictionary<string, string> serverResponse = await ExecuteRequest(endpoint, parameters, cancellationToken);
            response = BuildResponse(messages, serverResponse);
        }
        
        // Dump error message if exception occurred
        catch (Exception e)
        {
            string errorMessage = $"### ReviDotNet.GenerateAsync() Error Generating Chat Completion\n" +
                                  $"# URL\n{_client.BaseAddress + endpoint}\n\n" +
                                  $"# Payload\n{payloadDebug}\n\n" +
                                  $"# Exception\n{e.Message}\n\n";
            Util.Log(errorMessage);
            await Util.DumpLog(errorMessage, "ic-generate-chat-error");
            throw;
        }

        // Dump successful logging if successful
        string responseDebug = $"'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''";
        string dumpMessage = $"### ReviDotNet.GenerateAsync() Chat Completion\n" +
                             $"# URL\n{_client.BaseAddress + endpoint}\n\n" +
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_supportsCompletion is false)
            throw new Exception("Attempting prompt completion on provider that does not support it");

        model = model == "default" ? _defaultModel : model;
        var parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "prompt", prompt },
            { "stream", true }
        };

        AddOptionalParameters(
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
            guidanceString);
        
        // Use appropriate endpoint based on protocol
        string endpoint;
        if (_protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:streamGenerateContent";
        else
            endpoint = "v1/completions";

        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";
        
        return CreateStreamingResultWrapper(
            ExecuteStreamingRequest(endpoint, parameters, cancellationToken),
            cancellationToken);
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        model = model == "default" ? _defaultModel : model;
        var parameters = new Dictionary<string, object>
        {
            { "model", model },
            { "messages", messages },
            { "stream", true }
        };

        AddOptionalParameters(
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
            guidanceString);

        string endpoint;
        if (_protocol == Protocol.Gemini)
            endpoint = $"v1beta/models/{model}:streamGenerateContent";
        else
            endpoint = "v1/chat/completions";

        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";

        return CreateStreamingResultWrapper(
            ExecuteStreamingRequest(endpoint, parameters, cancellationToken),
            cancellationToken);
    }
    #endregion
    
    
    // ===================
    //  Stream Requesting
    // ===================
    
    #region Streaming Requesting
    /// <summary>
    /// Creates a StreamingResult wrapper that tracks metadata without breaking streaming.
    /// </summary>
    private StreamingResult<string> CreateStreamingResultWrapper(
        IAsyncEnumerable<string> sourceStream,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var metadataTracker = new StreamingMetadataTracker(startTime);
    
        var trackedStream = TrackStreamingMetadata(sourceStream, metadataTracker, cancellationToken);
    
        return new StreamingResult<string>
        {
            Stream = trackedStream,
            Completion = metadataTracker.CompletionTask
        };
    }

    /// <summary>
    /// Tracks streaming metadata without buffering the stream.
    /// </summary>
    private async IAsyncEnumerable<string> TrackStreamingMetadata(
        IAsyncEnumerable<string> sourceStream,
        StreamingMetadataTracker tracker,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = sourceStream.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await MoveNextSafely(enumerator, tracker))
            {
                yield return enumerator.Current;
            }
            tracker.CompleteSuccessfully();
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// Safely moves to the next item and handles errors.
    /// </summary>
    private async Task<bool> MoveNextSafely(
        IAsyncEnumerator<string> enumerator, 
        StreamingMetadataTracker tracker)
    {
        try
        {
            var hasNext = await enumerator.MoveNextAsync();
            if (hasNext)
            {
                tracker.IncrementChunkCount();
            }
            return hasNext;
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
        {
            tracker.CompleteCanceled(ex);
            throw;
        }
        catch (Exception ex)
        {
            tracker.CompleteWithError(ex);
            throw;
        }
    }



    /// <summary>
    /// Executes a streaming request to the AI inference service with the specified payload and cancellation token.
    /// </summary>
    /// <param name="endpoint">The endpoint of the AI inference service.</param>
    /// <param name="payload">The payload to be sent to the AI inference service.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the request.</param>
    /// <returns>An async enumerable that yields streaming text chunks.</returns>
    private async IAsyncEnumerable<string> ExecuteStreamingRequest(
        string endpoint,
        Dictionary<string, object> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _clientSemaphore.WaitAsync(cancellationToken);
        try
        {
            await EnsureRateLimit();
            
            // Handle Gemini-specific payload transformation
            if (_protocol == Protocol.Gemini)
            {
                payload = TransformToGeminiPayload(payload);
                // Add API key to endpoint for Gemini
                if (_useApiKey && !endpoint.Contains("key="))
                {
                    endpoint += (endpoint.Contains("?") ? "&" : "?") + $"key={_apiKey}";
                }
            }
            
            var content = new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8,
                "application/json");
                
            await foreach (var chunk in MakeStreamingRequestAsync(endpoint, content, cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Sends a streaming HTTP POST request to the specified API endpoint and processes the Server-Sent Events (SSE) response.
    /// </summary>
    /// <param name="endpoint">The API endpoint to which the request is sent.</param>
    /// <param name="content">The HTTP request content to be sent in the POST request.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>An async enumerable that yields text chunks from the streaming response.</returns>
    private async IAsyncEnumerable<string> MakeStreamingRequestAsync(
        string endpoint,
        StringContent content,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        
        try
        {
            response = await EstablishStreamingConnection(endpoint, content, cancellationToken);
            
            await foreach (var chunk in ProcessStreamingResponse(response, cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// Establishes a streaming connection with retry logic.
    /// </summary>
    private async Task<HttpResponseMessage> EstablishStreamingConnection(
        string endpoint,
        StringContent content,
        CancellationToken cancellationToken)
    {
        int retryAttempt = 0;
        HttpResponseMessage? response = null;

        while (retryAttempt <= _retryAttemptLimit)
        {
            try
            {
                response?.Dispose(); // Dispose previous attempt if any
                
                response = await _client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content },
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                    return response;
                
                // Handle non-success status codes
                string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                
                if (retryAttempt >= _retryAttemptLimit)
                {
                    string errorMessage = $"Streaming API request failed after {retryAttempt} retries: \n" +
                                         $" - Reason: {response.ReasonPhrase} ({(int)response.StatusCode})\n" +
                                         $" - Message: '{responseContent}'\n";
                    
                    Util.Log(errorMessage);
                    await Util.DumpLog(errorMessage, "ic-streaming-api-failure");
                    throw new Exception(errorMessage);
                }
                
                // Calculate delay for retry
                double delaySeconds = _retryInitialDelaySeconds * Math.Pow(2, retryAttempt);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                retryAttempt++;
            }
            catch (Exception ex) when (retryAttempt < _retryAttemptLimit)
            {
                response?.Dispose();
                
                double delaySeconds = _retryInitialDelaySeconds * Math.Pow(2, retryAttempt);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                retryAttempt++;
                
                if (retryAttempt > _retryAttemptLimit)
                    throw;
            }
        }

        response?.Dispose();
        throw new Exception("Failed to establish streaming connection after all retries");
    }

    /// <summary>
    /// Processes the streaming response and yields individual chunks.
    /// </summary>
    private async IAsyncEnumerable<string> ProcessStreamingResponse(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            // Handle Server-Sent Events format
            if (line.StartsWith("data: "))
            {
                var chunk = ProcessStreamingChunk(line);
                if (!string.IsNullOrEmpty(chunk))
                {
                    yield return chunk;
                }
            }
        }
    }

    /// <summary>
    /// Processes a single streaming chunk and extracts the text content.
    /// </summary>
    /// <param name="data">The raw JSON data from the streaming response.</param>
    /// <returns>The extracted text content from the chunk.</returns>
    private string ProcessStreamingChunk(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return string.Empty;

        try
        {
            var jsonData = JsonConvert.DeserializeObject<Dictionary<string, JsonElement>>(data);
            if (jsonData == null)
                return string.Empty;

            // Handle Gemini streaming response format
            if (_protocol == Protocol.Gemini)
            {
                if (jsonData.TryGetValue("candidates", out var candidates) && 
                    candidates.ValueKind == JsonValueKind.Array && 
                    candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array && 
                        parts.GetArrayLength() > 0)
                    {
                        var firstPart = parts[0];
                        if (firstPart.TryGetProperty("text", out var textElement))
                        {
                            return textElement.GetString() ?? string.Empty;
                        }
                    }
                }
            }
            else
            {
                // Standard OpenAI/vLLM streaming format
                if (jsonData.TryGetValue("choices", out var choices) && 
                    choices.ValueKind == JsonValueKind.Array && 
                    choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    
                    // Handle completion streaming (prompt-based)
                    if (firstChoice.TryGetProperty("text", out var textElement))
                    {
                        return textElement.GetString() ?? string.Empty;
                    }
                    
                    // Handle chat completion streaming
                    if (firstChoice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var contentElement))
                        {
                            return contentElement.GetString() ?? string.Empty;
                        }
                        
                        // Handle message role changes
                        if (delta.TryGetProperty("role", out var roleElement))
                        {
                            // Role changes don't contain content to yield
                            return string.Empty;
                        }
                    }
                }
            }
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            Util.Log($"Warning: Failed to parse streaming JSON chunk: {ex.Message}");
        }

        return string.Empty;
    }
    #endregion
    
    
    // =================
    //  Http Requesting
    // =================
    
    #region Http Requesting
    /// <summary>
    /// Executes a request to the AI inference service with the specified payload and cancellation token.
    /// </summary>
    /// <param name="endpoint">The endpoint of the AI inference service.</param>
    /// <param name="payload">The payload to be sent to the AI inference service. It should contain the necessary parameters for the request.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the request.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a dictionary of the response parameters.</returns>
    private async Task<Dictionary<string, string>> ExecuteRequest(
        string endpoint,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken)
    {
        await _clientSemaphore.WaitAsync(cancellationToken);
        try
        {
            await EnsureRateLimit();
            
            // Handle Gemini-specific payload transformation
            if (_protocol == Protocol.Gemini)
            {
                payload = TransformToGeminiPayload(payload);
                // Add API key to endpoint for Gemini
                if (_useApiKey && !endpoint.Contains("key="))
                {
                    endpoint += (endpoint.Contains("?") ? "&" : "?") + $"key={_apiKey}";
                }
            }
            
            var content = new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8,
                "application/json");
            return await MakeRequestAsync(endpoint, content, cancellationToken);
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }

    /// <summary>
    /// Sends an asynchronous HTTP POST request to the specified API endpoint with the given content.
    /// Implements a retry mechanism with exponential back-off in case of failures.
    /// </summary>
    /// <param name="endpoint">The API endpoint to which the request is sent.</param>
    /// <param name="content">The HTTP request content to be sent in the POST request.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>A dictionary object containing the parsed key-value pairs from the API response.</returns>
    /// <remarks>
    /// If the request fails due to reasons such as a non-success status code, the method retries with an exponential back-off
    /// delay until the maximum retry limit is reached. An exception is thrown if all retry attempts fail.
    /// </remarks>
    private async Task<Dictionary<string, string>> MakeRequestAsync(
        string endpoint,
        StringContent content,
        CancellationToken cancellationToken)
    {
        int retryAttempt = 0; // Counter for the current attempt number

        HttpResponseMessage response = await _client.PostAsync(endpoint, content, cancellationToken);
        
        // We got an unsuccessful response back... try again? 
        while (!response.IsSuccessStatusCode)
        {
            // Get the error message
            string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            string errorMessage;
            
            // End if we're at our retry attempt limit
            if (retryAttempt >= _retryAttemptLimit)
            {
                errorMessage = $"API request failed after {retryAttempt} retries: \n" +
                               $" - Reason: {response.ReasonPhrase} ({(int)response.StatusCode})\n" +
                               $" - Message: '{responseContent}'\n";
                
                Util.Log(errorMessage);
                await Util.DumpLog(
                    errorMessage +
                    $" - Response:\n'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''\n",
                    "ic-api-failure");
                
                throw new Exception(errorMessage);
            }
            
            // Calculate the delay using exponential back-off
            double delaySeconds = _retryInitialDelaySeconds * Math.Pow(2, retryAttempt);
            errorMessage = $"API request failed, trying again in {delaySeconds} seconds:\n" +
                           $" - URI: {_client.BaseAddress + endpoint}\n" +
                           $" - Reason: {response.ReasonPhrase} ({(int)response.StatusCode})\n" +
                           $" - Message: '{responseContent}'\n";

            Util.Log(errorMessage);
            
            // Delay the next attempt
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            
            // Increase retry attempt
            retryAttempt++;

            // Try again
            response = await _client.PostAsync(endpoint, content, cancellationToken);
        }

        return ProcessHttpResponse(response);
    }

    /// <summary>
    /// Processes the HTTP response and extracts the required information.
    /// </summary>
    /// <param name="response">The HTTP response returned by the server.</param>
    /// <returns>A dictionary containing the extracted information from the response.</returns>
    private Dictionary<string, string> ProcessHttpResponse(HttpResponseMessage response)
    {
        var data = response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>().Result;
        //Util.Log($"Response: {System.Text.Json.JsonSerializer.Serialize(data)}");
        
        if (data == null)
            throw new Exception($"ProcessHttpResponse: Invalid response (data null):\n'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''\n");

        var result = new Dictionary<string, string>();

        // Handle Gemini response format
        if (_protocol == Protocol.Gemini)
        {
            if (data.TryGetValue("candidates", out var candidates) && 
                candidates.ValueKind == JsonValueKind.Array)
            {
                var firstCandidate = candidates[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    var firstPart = parts[0];
                    if (firstPart.TryGetProperty("text", out var textElement))
                    {
                        result.Add("text", textElement.GetString() ?? "");
                    }
                }
                
                if (firstCandidate.TryGetProperty("finishReason", out var finishReason))
                {
                    result.Add("finish_reason", finishReason.GetString() ?? string.Empty);
                }
            }
        }
        else
        {
            // Standard OpenAI/vLLM format
            if (!data.TryGetValue("choices", out var choices))
                throw new Exception($"ProcessHttpResponse: Invalid response (missing choices):\n'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''\n");

            if (choices[0].TryGetProperty("text", out var textElement))
            {
                result.Add("text", textElement.GetString() ?? "");
            }
            else
            {
                // Extracting message content from the chat completion response
                if (choices[0].TryGetProperty("message", out var messageElement) &&
                    messageElement.TryGetProperty("content", out var contentElement))
                {
                    result.Add("text", contentElement.GetString() ?? "");
                }
            }
            
            if (choices[0].TryGetProperty("finish_reason", out var finishReason))
                result.Add("finish_reason", finishReason.GetString() ?? string.Empty);
        }

        return result;
    }
    #endregion
    
    
    // ======================
    //  Supporting Functions
    // ======================
    
    #region Supporting Functions
    /// <summary>
    /// Disposes the HttpClient instance.
    /// </summary>
    public void Dispose()
    {
        _client.Dispose();
    }

    /// <summary>
    /// Transforms the standard payload format to Gemini's expected format.
    /// </summary>
    /// <param name="payload">The standard payload dictionary.</param>
    /// <returns>The transformed payload for Gemini API.</returns>
    private Dictionary<string, object> TransformToGeminiPayload(Dictionary<string, object> payload)
    {
        var geminiPayload = new Dictionary<string, object>();
        var generationConfig = new Dictionary<string, object>();
        
        // Transform generation config parameters
        if (payload.TryGetValue("temperature", out var temperature))
            generationConfig["temperature"] = temperature;
        
        if (payload.TryGetValue("top_k", out var topK))
            generationConfig["topK"] = topK;
        
        if (payload.TryGetValue("top_p", out var topP))
            generationConfig["topP"] = topP;
        
        if (payload.TryGetValue("min_p", out var minP))
            generationConfig["minP"] = minP;
        
        if (payload.TryGetValue("max_tokens", out var maxTokens))
            generationConfig["maxOutputTokens"] = maxTokens;
        
        if (payload.TryGetValue("stop", out var stopSequences))
            generationConfig["stopSequences"] = stopSequences;

        // Handle Gemini JSON Schema (responseSchema)
        if (payload.TryGetValue("guided_json", out var jsonSchema))
        {
            try
            {
                // Parse the JSON schema string to ensure it's valid JSON
                var schemaObject = JsonConvert.DeserializeObject(jsonSchema.ToString());
                generationConfig["responseSchema"] = schemaObject;
                generationConfig["responseMimeType"] = "application/json";
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // If parsing fails, treat it as a string (though this shouldn't happen with valid JSON schema)
                Util.Log($"Warning: Invalid JSON schema provided for Gemini: {jsonSchema}");
            }
        }
        
        if (generationConfig.Any())
            geminiPayload["generationConfig"] = generationConfig;
        
        // Handle single prompt
        if (payload.TryGetValue("prompt", out var promptValue) && promptValue is string prompt)
        {
            geminiPayload["contents"] = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            };
        }
        // Handle chat messages
        else if (payload.TryGetValue("messages", out var messagesValue) && messagesValue is List<Message> messages)
        {
            Message? systemMessage = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
            if (systemMessage != null)
            {
                geminiPayload["systemInstruction"] = new { parts = new[] { new { text = systemMessage.Content } } };
            }

            var contents = messages
                .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(m => new
                {
                    role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : m.Role.ToLower(),
                    parts = new[] { new { text = m.Content } }
                })
                .ToList();
            
            geminiPayload["contents"] = contents;
        }
        
        // Note: Gemini doesn't support frequency_penalty, presence_penalty, or best_of
        // These parameters are ignored for Gemini requests

        return geminiPayload;
    }

    /// <summary>
    /// Adds optional parameters to the specified dictionary.
    /// </summary>
    /// <param name="parameters">The dictionary to add the optional parameters to.</param>
    /// <param name="temperature">The temperature parameter value.</param>
    /// <param name="topP">The top_p parameter value.</param>
    /// <param name="topK">The top_k parameter value.</param>
    /// <param name="bestOf">The best_of parameter value.</param>
    /// <param name="maxTokens">The max_tokens parameter value.</param>
    /// <param name="frequencyPenalty">The frequencyPenalty parameter value.</param>
    /// <param name="presencePenalty">The presencePenalty parameter value.</param>
    /// <param name="stopSequences">The stopSequences parameter value.</param>
    /// <param name="guidanceType">The guidanceType parameter value.</param>
    /// <param name="guidanceString">The guidanceString parameter value.</param>
    private void AddOptionalParameters(
        Dictionary<string, object> parameters,
        float? temperature,
        int? topK,
        float? topP,
        float? minP,
        int? bestOf,
        MaxTokenType? maxTokenType,
        int? maxTokens,
        float? frequencyPenalty,
        float? presencePenalty,
        float? repetitionPenalty,
        string[]? stopSequences,
        GuidanceType? guidanceType,
        string? guidanceString)
    {
        if (temperature.HasValue) parameters.Add("temperature", temperature.Value);
        if (topK.HasValue) parameters.Add("top_k", topK.Value);
        if (topP.HasValue) parameters.Add("top_p", topP.Value);
        if (minP.HasValue) parameters.Add("min_p", minP.Value);
        if (frequencyPenalty.HasValue) parameters.Add("frequency_penalty", frequencyPenalty.Value);
        if (presencePenalty.HasValue) parameters.Add("presence_penalty", presencePenalty.Value);
        if (repetitionPenalty.HasValue) parameters.Add("repetition_penalty", repetitionPenalty.Value);
        if (stopSequences is { Length: > 0 }) parameters.Add("stop", stopSequences);

        GuidanceType? chosenType = guidanceType ?? _defaultGuidanceType;
        string? chosenString = guidanceString ?? _defaultGuidanceString;
        //Util.Log($"guidanceString: {guidanceString}, _protocol: {_protocol}");
        
        switch (maxTokenType)
        {
            case MaxTokenType.MaxTokens:
                if (maxTokens.HasValue) parameters.Add("max_tokens", maxTokens.Value);
                break;
            
            case MaxTokenType.MaxCompletionTokens:
                if (maxTokens.HasValue) parameters.Add("max_completion_tokens", maxTokens.Value);
                break;
        }
        
        switch (_protocol)
        {
            case Protocol.OpenAI:
            {
                // OpenAI Parameters - no bestOf support
                
                // OpenAI JSON Schema Guidance
                if (!_supportsGuidance || string.IsNullOrEmpty(chosenString) || chosenType != GuidanceType.Json)
                {
                    return;
                }

                try
                {
                    // Parse the JSON schema string to ensure it's valid JSON
                    var processedSchema = Util.AddAdditionalPropertiesToSchema(chosenString);

                    
                    // OpenAI uses response_format with json_schema type
                    parameters.Add("response_format", new
                    {
                        type = "json_schema",
                        json_schema = new
                        {
                            name = "response_schema",
                            strict = true,
                            schema = processedSchema
                        }
                    });
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    Util.Log($"Warning: Invalid JSON schema provided for OpenAI: {chosenString}. Error: {ex.Message}");
                }
                
                break;
            }

            case Protocol.vLLM:
            {
                // vLLM Parameters
                if (bestOf.HasValue) parameters.Add("best_of", bestOf.Value);

                // vLLM Guidance
                if (!_supportsGuidance || string.IsNullOrEmpty(chosenString))
                {
                    //Util.Log($"{_supportsGuidance} : {chosenString}");
                    return;
                }

                switch (chosenType)
                {
                    case GuidanceType.Json:
                        parameters.Add("guided_json", chosenString);
                        //parameters.Add("guided_decoding_backend", "outlines");
                        parameters.Add("guided_decoding_backend", "outlines");
                        break;
                    case GuidanceType.Regex:
                        parameters.Add("guided_regex", chosenString);
                        parameters.Add("guided_decoding_backend", "lm-format-enforcer");
                        //parameters.Add("guided_decoding_backend", "outlines");
                        break;
                    /*case GuidanceType.Choice:
                        guidance.Add("guided_choice", _defaultGuidance);
                        guidance.Add("guided_decoding_backend", "lm-format-enforcer");
                        break;*/
                }

                break;
            }

            case Protocol.LLamaAPI:
            {
                // LLamaAPI Parameters
                
                // LLamaAPI Guidance
                if (!_supportsGuidance || string.IsNullOrEmpty(chosenString)) 
                    return;
                
                switch (chosenType)
                {
                    case GuidanceType.Json:
                        parameters.Add("json_schema", chosenString);
                        break;
                    case GuidanceType.Grammar:
                        parameters.Add("grammar", chosenString);
                        break;
                }

                break;
            }

            case Protocol.Gemini:
            {
                // Gemini doesn't support bestOf, frequencyPenalty, presencePenalty
                // Note: Gemini doesn't support regex guidance or other guidance types
                // Parameters will be transformed in TransformToGeminiPayload method
                
                // Gemini JSON Schema Guidance
                if (!_supportsGuidance || string.IsNullOrEmpty(chosenString) || chosenType != GuidanceType.Json)
                {
                    //Util.Log($"{_supportsGuidance} : {chosenString}");
                    return;
                }
                
                // Add guided_json parameter which will be transformed to responseSchema in TransformToGeminiPayload
                parameters.Add("guided_json", chosenString);
                break;
            }
        }
    }
    
    /// <summary>
    /// Ensures that the rate limit between requests is met before making a request.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task EnsureRateLimit()
    {
        var elapsed = DateTime.Now - _lastExecutionTime;
        if (elapsed.TotalMilliseconds < _delayBetweenRequestsMs)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_delayBetweenRequestsMs) - elapsed);
        }
        _lastExecutionTime = DateTime.Now;
    }
    #endregion
}