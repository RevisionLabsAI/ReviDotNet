// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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
/// Provides asynchronous client functionality for interacting with an AI embeddings service.
/// </summary>
public class EmbedClient : IDisposable
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
    #endregion
    

    // =============
    //  Constructor
    // =============
    
    #region Constructor
    /// <summary>
    /// Initializes a new instance of the EmbedClient class that handles requests to an AI embeddings API.
    /// </summary>
    /// <param name="apiUrl">The base URL of the AI embeddings API.</param>
    /// <param name="apiKey">The API key used for authenticating with the embeddings API. If not specified, authentication is disabled.</param>
    /// <param name="protocol">The protocol to use (OpenAI or Gemini).</param>
    /// <param name="defaultModel">The identifier for the default model used for embedding requests. Default is "text-embedding-ada-002".</param>
    /// <param name="timeoutSeconds">The timeout in seconds for HTTP requests to the API. Default is 100 seconds.</param>
    /// <param name="delayBetweenRequestsMs">The minimum delay in milliseconds between subsequent requests to the API to avoid rate limits. Default is 0 ms.</param>
    /// <param name="retryAttemptLimit">The maximum number of retry attempts for a request in case of failures. Default is 5 attempts.</param>
    /// <param name="retryInitialDelaySeconds">The initial delay in seconds before the first retry attempt. Default is 5 seconds.</param>
    /// <param name="simultaneousRequests">The maximum number of simultaneous requests allowed to the API. Default is 10.</param>
    /// <param name="httpClientOverride">Optional HttpClient to use instead of creating a new one (for testing purposes).</param>
    public EmbedClient(
        string apiUrl,
        string apiKey = "",
        Protocol protocol = Protocol.OpenAI,
        string defaultModel = "text-embedding-ada-002",
        int timeoutSeconds = 100,
        int delayBetweenRequestsMs = 0,
        int retryAttemptLimit = 5,
        int retryInitialDelaySeconds = 5,
        int simultaneousRequests = 10,
        HttpClient? httpClientOverride = null)
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
        
        // HTTP Client Setup
        _protocol = protocol;
        apiUrl = apiUrl.TrimEnd('/');
        
        if (apiUrl.EndsWith("/v1/embeddings"))
            throw new Exception("Please remove /v1/embeddings from the end of API URL");
        
        // Use provided HttpClient if available (for testing), otherwise create a new one
        _client = httpClientOverride ?? new HttpClient { BaseAddress = new Uri(apiUrl) };
        if (_client.BaseAddress is null)
        {
            _client.BaseAddress = new Uri(apiUrl);
        }
        
        _client.DefaultRequestHeaders.Add("Accept", "application/json");
        
        if (_useApiKey)
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        
        _client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
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
    /// Gets the appropriate endpoint path for the embedding request based on protocol.
    /// </summary>
    /// <returns>The endpoint path string.</returns>
    private string GetEmbeddingEndpoint()
    {
        switch (_protocol)
        {
            case Protocol.OpenAI:
                return "v1/embeddings";
            
            case Protocol.Gemini:
                // Gemini uses a different endpoint structure
                return "v1beta/models/{model}:embedContent";
            
            default:
                return "v1/embeddings";
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

    /// <summary>
    /// Processes the HTTP response and extracts embedding data.
    /// </summary>
    /// <param name="response">The HTTP response returned by the server.</param>
    /// <returns>An EmbeddingResponse object containing the embedding vectors.</returns>
    private EmbeddingResponse ProcessHttpResponse(HttpResponseMessage response)
    {
        var data = response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>().Result;
        Util.Log($"Response: {System.Text.Json.JsonSerializer.Serialize(data)}");
        
        if (data == null)
            throw new Exception("Invalid server response: null data");

        var embeddingResponse = new EmbeddingResponse
        {
            Data = new List<EmbeddingData>(),
            Inputs = new List<string>(),
            Usage = new Dictionary<string, int>()
        };

        // Handle different protocol response formats
        switch (_protocol)
        {
            case Protocol.OpenAI:
                return ProcessOpenAIResponse(data);
            
            case Protocol.Gemini:
                return ProcessGeminiResponse(data);
            
            default:
                return ProcessOpenAIResponse(data); // Default to OpenAI format
        }
    }

    /// <summary>
    /// Processes OpenAI-format embedding response.
    /// </summary>
    private EmbeddingResponse ProcessOpenAIResponse(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("data", out var dataArray))
            throw new Exception("Invalid OpenAI embedding response: missing 'data' field");

        var embeddingResponse = new EmbeddingResponse
        {
            Data = new List<EmbeddingData>(),
            Inputs = new List<string>(),
            Model = data.TryGetValue("model", out var modelElement) ? modelElement.GetString() ?? "" : "",
            Object = data.TryGetValue("object", out var objectElement) ? objectElement.GetString() ?? "list" : "list"
        };

        // Parse usage if available
        if (data.TryGetValue("usage", out var usageElement))
        {
            embeddingResponse.Usage = new Dictionary<string, int>();
            if (usageElement.TryGetProperty("prompt_tokens", out var promptTokens))
                embeddingResponse.Usage["prompt_tokens"] = promptTokens.GetInt32();
            if (usageElement.TryGetProperty("total_tokens", out var totalTokens))
                embeddingResponse.Usage["total_tokens"] = totalTokens.GetInt32();
        }

        // Parse embedding data
        foreach (var item in dataArray.EnumerateArray())
        {
            var embeddingData = new EmbeddingData
            {
                Index = item.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : 0,
                Object = item.TryGetProperty("object", out var objElement) ? objElement.GetString() ?? "embedding" : "embedding"
            };

            if (item.TryGetProperty("embedding", out var embeddingElement))
            {
                var embeddingList = new List<float>();
                foreach (var value in embeddingElement.EnumerateArray())
                {
                    embeddingList.Add((float)value.GetDouble());
                }
                embeddingData.Embedding = embeddingList.ToArray();
            }

            embeddingResponse.Data.Add(embeddingData);
        }

        return embeddingResponse;
    }

    /// <summary>
    /// Processes Gemini-format embedding response.
    /// </summary>
    private EmbeddingResponse ProcessGeminiResponse(Dictionary<string, JsonElement> data)
    {
        var embeddingResponse = new EmbeddingResponse
        {
            Data = new List<EmbeddingData>(),
            Inputs = new List<string>(),
            Model = "gemini",
            Object = "list"
        };

        // Gemini returns embedding in a different format
        if (data.TryGetValue("embedding", out var embeddingElement))
        {
            var embeddingData = new EmbeddingData
            {
                Index = 0,
                Object = "embedding"
            };

            if (embeddingElement.TryGetProperty("values", out var valuesElement))
            {
                var embeddingList = new List<float>();
                foreach (var value in valuesElement.EnumerateArray())
                {
                    embeddingList.Add((float)value.GetDouble());
                }
                embeddingData.Embedding = embeddingList.ToArray();
            }

            embeddingResponse.Data.Add(embeddingData);
        }

        return embeddingResponse;
    }
    
    /// <summary>
    /// Makes an asynchronous HTTP POST request with the provided content to the API.
    /// </summary>
    /// <param name="content">The HTTP content to send with the request.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>An EmbeddingResponse containing the embedding vectors from the API.</returns>
    private async Task<EmbeddingResponse> MakeRequestAsync(
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
            var errorMessage = await response.Content.ReadAsStringAsync(cancellationToken);
            
            // End if we're at our retry attempt limit
            if (retryAttempt >= _retryAttemptLimit)
            {
                throw new Exception(
                    $"API request failed after {retryAttempt} retries: " +
                    $"{response.StatusCode} {response.ReasonPhrase}. Details: {errorMessage}");
            }
            
            // Calculate the delay using exponential back-off
            double delaySeconds = _retryInitialDelaySeconds * Math.Pow(2, retryAttempt);
            Util.Log($"API request failed: {response.StatusCode}: {errorMessage}\nTrying again in {delaySeconds} seconds.");

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
    /// Executes a request to the AI embeddings service with the specified payload and cancellation token.
    /// </summary>
    /// <param name="endpoint">The endpoint of the AI embeddings service.</param>
    /// <param name="payload">The payload to be sent to the AI embeddings service. It should contain the necessary parameters for the request.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the request.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains an EmbeddingResponse.</returns>
    private async Task<EmbeddingResponse> ExecuteRequest(
        string endpoint,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken)
    {
        await _clientSemaphore.WaitAsync(cancellationToken);
        try
        {
            await EnsureRateLimit();
            var content = new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8,
                "application/json");
            return await MakeRequestAsync(endpoint, content, cancellationToken);
        }
        finally
        {
            _clientSemaphore.Release();
        }
    }
    #endregion
    
    
    // ======================
    //  Embedding Functions 
    // ======================
    
    #region Embedding Functions
    /// <summary>
    /// Generates embeddings for a single text input.
    /// </summary>
    /// <param name="input">The text to generate embeddings for.</param>
    /// <param name="model">The model identifier to use for the request. Defaults to the configured default model.</param>
    /// <param name="dimensions">The number of dimensions for the embedding (if supported by the model).</param>
    /// <param name="encodingFormat">The format of the encoding (e.g., "float" or "base64").</param>
    /// <param name="cancellationToken">Cancellation token to cancel the request.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the <see cref="EmbeddingResponse"/> object.</returns>
    public async Task<EmbeddingResponse> GenerateEmbeddingAsync(
        string input,
        string model = "default",
        int? dimensions = null,
        string? encodingFormat = null,
        CancellationToken cancellationToken = default)
    {
        return await GenerateEmbeddingsAsync(new[] { input }, model, dimensions, encodingFormat, cancellationToken);
    }

    /// <summary>
    /// Generates embeddings for multiple text inputs.
    /// </summary>
    /// <param name="inputs">The array of texts to generate embeddings for.</param>
    /// <param name="model">The model identifier to use for the request. Defaults to the configured default model.</param>
    /// <param name="dimensions">The number of dimensions for the embedding (if supported by the model).</param>
    /// <param name="encodingFormat">The format of the encoding (e.g., "float" or "base64").</param>
    /// <param name="cancellationToken">Cancellation token to cancel the request.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the <see cref="EmbeddingResponse"/> object.</returns>
    public async Task<EmbeddingResponse> GenerateEmbeddingsAsync(
        string[] inputs,
        string model = "default",
        int? dimensions = null,
        string? encodingFormat = null,
        CancellationToken cancellationToken = default)
    {
        model = model == "default" ? _defaultModel : model;

        // Build the request based on protocol
        Dictionary<string, object> parameters;
        string endpoint;

        switch (_protocol)
        {
            case Protocol.OpenAI:
                endpoint = GetEmbeddingEndpoint();
                parameters = new Dictionary<string, object>
                {
                    { "model", model },
                    { "input", inputs.Length == 1 ? (object)inputs[0] : inputs }
                };

                if (dimensions.HasValue)
                    parameters.Add("dimensions", dimensions.Value);
                
                if (!string.IsNullOrEmpty(encodingFormat))
                    parameters.Add("encoding_format", encodingFormat);
                
                break;

            case Protocol.Gemini:
                // Gemini has different endpoint and payload structure
                endpoint = GetEmbeddingEndpoint().Replace("{model}", model);
                
                // Gemini expects different payload format
                parameters = new Dictionary<string, object>
                {
                    { "content", new Dictionary<string, object>
                        {
                            { "parts", new[] { new Dictionary<string, string> { { "text", inputs[0] } } } }
                        }
                    }
                };
                
                // Note: Gemini currently doesn't support batch embeddings in the same way
                if (inputs.Length > 1)
                {
                    Util.Log("Warning: Gemini embedding client processes only the first input in batch requests");
                }
                
                break;

            default:
                // Default to OpenAI format
                endpoint = GetEmbeddingEndpoint();
                parameters = new Dictionary<string, object>
                {
                    { "model", model },
                    { "input", inputs.Length == 1 ? (object)inputs[0] : inputs }
                };
                break;
        }

        Util.Log($"Embedding Payload:\n{JsonConvert.SerializeObject(parameters, Formatting.Indented)}");
        
        var response = await ExecuteRequest(endpoint, parameters, cancellationToken);
        
        // Store the original inputs in the response
        response.Inputs = inputs.ToList();
        
        return response;
    }
    #endregion
}
