using System.Runtime.CompilerServices;
using System.Text.Json;
using Newtonsoft.Json;

namespace Revi;

internal class StreamingProcessor
{
    private readonly InferClientConfig _config;
    private readonly SemaphoreSlim _clientSemaphore;
    private readonly RateLimiter _rateLimiter;
    private readonly PayloadTransformer _payloadTransformer;
    private readonly HttpClient _httpClient;

    public StreamingProcessor(InferClientConfig config, SemaphoreSlim clientSemaphore, RateLimiter rateLimiter, PayloadTransformer payloadTransformer, HttpClient httpClient)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clientSemaphore = clientSemaphore ?? throw new ArgumentNullException(nameof(clientSemaphore));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _payloadTransformer = payloadTransformer ?? throw new ArgumentNullException(nameof(payloadTransformer));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public void Dispose()
    {
        // Don't dispose _rateLimiter - it's owned by the main client
    }

    /// <summary>
    /// Creates a StreamingResult wrapper that tracks metadata without breaking streaming.
    /// </summary>
    public StreamingResult<string> CreateStreamingResultWrapper(
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
    public async IAsyncEnumerable<string> ExecuteStreamingRequest(
        string endpoint,
        Dictionary<string, object> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

        while (retryAttempt <= _config.RetryAttemptLimit)
        {
            try
            {
                response?.Dispose(); // Dispose previous attempt if any
                
                response = await _httpClient.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content },
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                    return response;
                
                // Handle non-success status codes
                string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                
                if (retryAttempt >= _config.RetryAttemptLimit)
                {
                    string errorMessage = $"Streaming API request failed after {retryAttempt} retries: \n" +
                                         $" - Reason: {response.ReasonPhrase} ({(int)response.StatusCode})\n" +
                                         $" - Message: '{responseContent}'\n";
                    
                    Util.Log(errorMessage);
                    await Util.DumpLog(errorMessage, "ic-streaming-api-failure");
                    throw new Exception(errorMessage);
                }
                
                // Calculate delay for retry
                double delaySeconds = _config.RetryInitialDelaySeconds * Math.Pow(2, retryAttempt);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                retryAttempt++;
            }
            catch (Exception ex) when (retryAttempt < _config.RetryAttemptLimit)
            {
                response?.Dispose();
                
                double delaySeconds = _config.RetryInitialDelaySeconds * Math.Pow(2, retryAttempt);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                retryAttempt++;
                
                if (retryAttempt > _config.RetryAttemptLimit)
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
            if (_config.Protocol == Protocol.Gemini)
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
}