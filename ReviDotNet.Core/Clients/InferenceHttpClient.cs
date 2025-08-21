using System.Net.Http.Json;
using System.Text.Json;
using Newtonsoft.Json;

namespace Revi;

internal class InferenceHttpClient : IDisposable
{
    private readonly InferClientConfig _config;
    private readonly SemaphoreSlim _clientSemaphore;
    private readonly RateLimiter _rateLimiter;
    private readonly PayloadTransformer _payloadTransformer;
    private readonly HttpClient _httpClient;

    public InferenceHttpClient(
        InferClientConfig config,
        SemaphoreSlim clientSemaphore,
        RateLimiter rateLimiter,
        PayloadTransformer payloadTransformer,
        HttpClient httpClient)
    {
        _config = config;
        _clientSemaphore = clientSemaphore;
        _rateLimiter = rateLimiter;
        _payloadTransformer = payloadTransformer;
        _httpClient = httpClient;
    }

    public void Dispose()
    {
        return;
    }
    
        /// <summary>
    /// Executes a request to the AI inference service with the specified payload and cancellation token.
    /// </summary>
    /// <param name="endpoint">The endpoint of the AI inference service.</param>
    /// <param name="payload">The payload to be sent to the AI inference service. It should contain the necessary parameters for the request.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the request.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a dictionary of the response parameters.</returns>
    public async Task<Dictionary<string, string>> ExecuteRequest(
        string endpoint,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken)
    {
        await _clientSemaphore.WaitAsync(cancellationToken);
        try
        {
            await _rateLimiter.EnsureRateLimit();
            
            // Handle Gemini-specific payload transformation
            if (_config.Protocol == Protocol.Gemini)
            {
                payload = _payloadTransformer.TransformToGeminiPayload(payload);
                // Add API key to endpoint for Gemini
                if (_config.UseApiKey && !endpoint.Contains("key="))
                {
                    endpoint += (endpoint.Contains("?") ? "&" : "?") + $"key={_config.ApiKey}";
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

        HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        
        // We got an unsuccessful response back... try again? 
        while (!response.IsSuccessStatusCode)
        {
            // Get the error message
            string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            string errorMessage;
            
            // End if we're at our retry attempt limit
            if (retryAttempt >= _config.RetryAttemptLimit)
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
            double delaySeconds = _config.RetryInitialDelaySeconds * Math.Pow(2, retryAttempt);
            errorMessage = $"API request failed, trying again in {delaySeconds} seconds:\n" +
                           $" - URI: {_httpClient.BaseAddress + endpoint}\n" +
                           $" - Reason: {response.ReasonPhrase} ({(int)response.StatusCode})\n" +
                           $" - Message: '{responseContent}'\n";

            Util.Log(errorMessage);
            
            // Delay the next attempt
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            
            // Increase retry attempt
            retryAttempt++;

            // Try again
            response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
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
        if (_config.Protocol == Protocol.Gemini)
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
}