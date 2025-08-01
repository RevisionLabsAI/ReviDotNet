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
    /// Generates predictions based on a single prompt with various optional parameters.
    /// </summary>
    /// <param name="prompt">The prompt to generate text from.</param>
    /// <param name="model">The model identifier to use for the request.</param>
    /// <param name="temperature">Control randomness. Lower values make responses more deterministic.</param>
    /// <param name="topP">Nucleus sampling: higher values cause more randomness.</param>
    /// <param name="topK">Limits the generated predictions to the top-k likely next words.</param>
    /// <param name="bestOf">Generates multiple outputs and selects the best one.</param>
    /// <param name="maxTokens">Maximum number of tokens to generate.</param>
    /// <param name="frequencyPenalty">Penalizes new tokens based on their frequency.</param>
    /// <param name="presencePenalty">Penalizes new tokens based on their presence.</param>
    /// <param name="stopSequences">Sequences where the model should stop generating further tokens.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the request.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the <see cref="CompletionResponse"/> object.</returns>
    public async Task<CompletionResponse> GenerateAsync(
        string prompt, 
        string model = "default", 
        double? temperature = null,
        double? topP = null, 
        int? topK = null, 
        int? bestOf = null,
        int? maxTokens = null, 
        double? frequencyPenalty = null,
        double? presencePenalty = null, 
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
            {"model", model},
            {"prompt", prompt}
        };

        AddOptionalParameters(
            parameters, 
            temperature, 
            topP, 
            topK,
            bestOf,
            maxTokens, 
            frequencyPenalty, 
            presencePenalty, 
            stopSequences,
            guidanceType,
            guidanceString);
        
        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";
        //Util.Log($"Payload:\n{payloadDebug}");
        
        Dictionary<string, string> serverResponse = await ExecuteRequest("v1/completions", parameters, cancellationToken);
        CompletionResponse response = BuildResponse(prompt, serverResponse);
        
        string responseDebug = $"'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''";
        string dumpMessage = $"### ReviDotNet.GenerateAsync() Prompt Completion\n" +
                             $"# URL\n{_client.BaseAddress + "v1/completions"}\n\n" +
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
    /// Generates an inference response asynchronously.
    /// </summary>
    /// <param name="messages">The list of messages to generate the response from.</param>
    /// <param name="model">The model identifier to use for the request.</param>
    /// <param name="temperature">Control randomness. Lower values make responses more deterministic.</param>
    /// <param name="topP">Nucleus sampling: higher values cause more randomness.</param>
    /// <param name="topK">Limits the generated predictions to the top-k likely next words.</param>
    /// <param name="bestOf">Generates multiple outputs and selects the best one.</param>
    /// <param name="maxTokens">Maximum number of tokens to generate.</param>
    /// <param name="frequencyPenalty">Penalizes new tokens based on their frequency.</param>
    /// <param name="presencePenalty">Penalizes new tokens based on their presence.</param>
    /// <param name="stopSequences">Sequences where the model should stop generating further tokens.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the request.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the <see cref="CompletionResponse"/> object.</returns>
    public async Task<CompletionResponse> GenerateAsync(
        List<Message> messages, 
        string model = "default",
        double? temperature = null,
        double? topP = null, 
        int? topK = null, 
        int? bestOf = null,
        int? maxTokens = null, 
        double? frequencyPenalty = null,
        double? presencePenalty = null, 
        string[]? stopSequences = null,
        GuidanceType? guidanceType = GuidanceType.Disabled,
        string? guidanceString = null,
        CancellationToken cancellationToken = default)
    {
        model = model == "default" ? _defaultModel : model;
        var parameters = new Dictionary<string, object>
        {
            {"model", model},
            {"messages", messages}
        };

        // Add optional parameters if they are not null
        AddOptionalParameters(
            parameters, 
            temperature, 
            topP, 
            topK,
            bestOf,
            maxTokens, 
            frequencyPenalty, 
            presencePenalty, 
            stopSequences,
            guidanceType,
            guidanceString);

        string payloadDebug = $"'''\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}\n'''";
        //Util.Log($"Payload:\n{payloadDebug}");
        
        string endpoint = _protocol == Protocol.Gemini ? GetGeminiChatEndpoint(model) : "v1/chat/completions";
        Dictionary<string, string> serverResponse = await ExecuteRequest(endpoint, parameters, cancellationToken);
        CompletionResponse response = BuildResponse(messages, serverResponse);
        
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


    #region Streaming Function
        /*
    public async IAsyncEnumerable<IList<string>> Stream(
        string systemPrompt,
        string userPrompt,
        string model,
        Dictionary<string, object>? paramaters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = FormatRequestData(systemPrompt, userPrompt, model:model, _apiKey, _useApiKey, false, @params, extraBody);
        
        var response = await client.PostAsJsonAsync("/v1/completions", payload, cancellationToken: cancellationToken);
        var content = await response.Content.ReadAsStreamAsync(cancellationToken); // TODO: code below not functional currently

        var buffer = new byte[32768];
        var filled = 0;

        for(;;)
        {
            var bytesRead = await content.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (bytesRead == 0)
            {
                if (filled > 0)
                {
                    throw new VllmChatClient("Unexpected end of stream");
                }

                break;
            }

            filled += bytesRead;

            for(;;)
            {
                var zero = Array.FindIndex(buffer, 0, filled, b => b == 0);
                if (zero < 0)
                {
                    if (filled == buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }

                    break;
                }

                var jsonDoc = JsonDocument.Parse(buffer.AsMemory(0, zero));
                var textItem = jsonDoc.RootElement.GetProperty("text");
                if (textItem.ValueKind != JsonValueKind.Array)
                {
                    throw new VllmChatClient("Invalid server response");
                }

                var texts = textItem.EnumerateArray().Select(v => v.GetString() ?? "N/A").ToList();
                yield return texts;

                var consumed = zero + 1;
                if (filled > consumed)
                {
                    buffer.AsSpan(consumed).CopyTo(buffer);
                }

                filled -= consumed;
            }
        }
    }
    */
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
    /// Makes an asynchronous HTTP POST request with the provided content to the API.
    /// </summary>
    /// <param name="content">The HTTP content to send with the request.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>A dictionary containing the response from the API.</returns>
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
    /// Gets the Gemini-specific chat endpoint for the specified model.
    /// </summary>
    /// <param name="model">The model identifier.</param>
    /// <returns>The Gemini chat endpoint URL.</returns>
    private string GetGeminiChatEndpoint(string model)
    {
        return $"v1beta/models/{model}:generateContent";
    }

    /// <summary>
    /// Transforms the standard payload format to Gemini's expected format.
    /// </summary>
    /// <param name="payload">The standard payload dictionary.</param>
    /// <returns>The transformed payload for Gemini API.</returns>
    private Dictionary<string, object> TransformToGeminiPayload(Dictionary<string, object> payload)
    {
        var geminiPayload = new Dictionary<string, object>();
        
        // Transform messages to Gemini's contents format
        if (payload.TryGetValue("messages", out var messagesObj) && messagesObj is List<Message> messages)
        {
            var contents = new List<object>();
            
            foreach (var message in messages)
            {
                var role = message.Role switch
                {
                    "system" => "user", // Gemini doesn't have system role, convert to user
                    "assistant" => "model",
                    _ => message.Role
                };
                
                contents.Add(new
                {
                    role = role,
                    parts = new[] { new { text = message.Content } }
                });
            }
            
            geminiPayload["contents"] = contents;
        }
        else if (payload.TryGetValue("prompt", out var promptObj))
        {
            // For prompt completion, convert to contents format
            geminiPayload["contents"] = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = promptObj.ToString() } }
                }
            };
        }

        // Transform generation config parameters
        var generationConfig = new Dictionary<string, object>();
        
        if (payload.TryGetValue("temperature", out var temperature))
            generationConfig["temperature"] = temperature;
        
        if (payload.TryGetValue("top_p", out var topP))
            generationConfig["topP"] = topP;
        
        if (payload.TryGetValue("top_k", out var topK))
            generationConfig["topK"] = topK;
        
        if (payload.TryGetValue("max_tokens", out var maxTokens))
            generationConfig["maxOutputTokens"] = maxTokens;
        
        if (payload.TryGetValue("stop", out var stopSequences))
            generationConfig["stopSequences"] = stopSequences;

        if (generationConfig.Count > 0)
            geminiPayload["generationConfig"] = generationConfig;

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
        double? temperature,
        double? topP,
        int? topK,
        int? bestOf,
        int? maxTokens,
        double? frequencyPenalty,
        double? presencePenalty,
        string[]? stopSequences,
        GuidanceType? guidanceType,
        string? guidanceString)
    {
        if (temperature.HasValue) parameters.Add("temperature", temperature.Value);
        if (topP.HasValue) parameters.Add("top_p", topP.Value);
        if (maxTokens.HasValue) parameters.Add("max_tokens", maxTokens.Value);
        if (frequencyPenalty.HasValue) parameters.Add("frequency_penalty", frequencyPenalty.Value);
        if (presencePenalty.HasValue) parameters.Add("presence_penalty", presencePenalty.Value);
        if (stopSequences is { Length: > 0 }) parameters.Add("stop", stopSequences);
        if (topK.HasValue) parameters.Add("top_k", topK.Value);

        GuidanceType? chosenType = guidanceType ?? _defaultGuidanceType;
        string? chosenString = guidanceString ?? _defaultGuidanceString;
        //Util.Log($"guidanceString: {guidanceString}, _protocol: {_protocol}");
        
        switch (_protocol)
        {
            case Protocol.OpenAI:
                // Nothing unique to do here
                break;

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
                // Guidance is not supported in the same way as other protocols
                // Parameters will be transformed in TransformToGeminiPayload method
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