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
        //Util.Log($"[DEBUG] ExecuteStreamingRequest started - Endpoint: {endpoint}");
        //Util.Log($"[DEBUG] Waiting for client semaphore...");
        
        await _clientSemaphore.WaitAsync(cancellationToken);
        try
        {
            //Util.Log($"[DEBUG] Semaphore acquired, checking rate limit...");
            await _rateLimiter.EnsureRateLimit();
            //Util.Log($"[DEBUG] Rate limit passed");
            
            // Handle Gemini-specific payload transformation
            if (_config.Protocol == Protocol.Gemini)
            {
                //Util.Log($"[DEBUG] Transforming payload for Gemini protocol");
                payload = _payloadTransformer.TransformToGeminiPayload(payload);
                // Add API key to endpoint for Gemini
                if (_config.UseApiKey && !endpoint.Contains("key="))
                {
                    endpoint += (endpoint.Contains("?") ? "&" : "?") + $"key={_config.ApiKey}";
                    //Util.Log($"[DEBUG] Added API key to endpoint");
                }
            }
            
            //Util.Log($"[DEBUG] Creating request content, payload size: {JsonConvert.SerializeObject(payload).Length} chars");
            var content = new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8,
                "application/json");
            
            //Util.Log($"[DEBUG] Starting streaming request...");
            int chunkCount = 0;
            await foreach (var chunk in MakeStreamingRequestAsync(endpoint, content, cancellationToken))
            {
                chunkCount++;
                //Util.Log($"[DEBUG] Yielding chunk #{chunkCount}, length: {chunk.Length}");
                yield return chunk;
            }
            //Util.Log($"[DEBUG] Streaming completed, total chunks: {chunkCount}");
        }
        finally
        {
            //Util.Log($"[DEBUG] Releasing semaphore");
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
        //Util.Log($"[DEBUG] EstablishStreamingConnection started");
        int retryAttempt = 0;
        HttpResponseMessage? response = null;

        while (retryAttempt <= _config.RetryAttemptLimit)
        {
            try
            {
                //Util.Log($"[DEBUG] Connection attempt #{retryAttempt + 1}");
                response?.Dispose(); // Dispose previous attempt if any
                
                //Util.Log($"[DEBUG] Sending HTTP request to: {endpoint}");
                response = await _httpClient.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content },
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                //Util.Log($"[DEBUG] HTTP response received - Status: {response.StatusCode} ({response.ReasonPhrase})");
                
                if (response.IsSuccessStatusCode)
                {
                    //Util.Log($"[DEBUG] Connection established successfully");
                    return response;
                }
                
                // Handle non-success status codes
                string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                //Util.Log($"[DEBUG] Non-success response content: {responseContent}");
                
                if (retryAttempt >= _config.RetryAttemptLimit)
                {
                    string errorMessage = $"Streaming API request failed after {retryAttempt} retries: \n" +
                                         $" - Reason: {response.ReasonPhrase} ({(int)response.StatusCode})\n" +
                                         $" - Message: '{responseContent}'\n";
                    
                    //Util.Log($"[DEBUG] Max retries exceeded, throwing exception");
                    Util.Log(errorMessage);
                    await Util.DumpLog(errorMessage, "ic-streaming-api-failure");
                    throw new Exception(errorMessage);
                }
                
                // Calculate delay for retry
                double delaySeconds = _config.RetryInitialDelaySeconds * Math.Pow(2, retryAttempt);
                //Util.Log($"[DEBUG] Retrying in {delaySeconds} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                retryAttempt++;
            }
            catch (Exception ex) when (retryAttempt < _config.RetryAttemptLimit)
            {
                //Util.Log($"[DEBUG] Exception during connection attempt: {ex.Message}");
                response?.Dispose();
                
                double delaySeconds = _config.RetryInitialDelaySeconds * Math.Pow(2, retryAttempt);
                //Util.Log($"[DEBUG] Retrying after exception in {delaySeconds} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                retryAttempt++;
                
                if (retryAttempt > _config.RetryAttemptLimit)
                    throw;
            }
        }

        response?.Dispose();
        //Util.Log($"[DEBUG] Failed to establish connection after all retries");
        throw new Exception("Failed to establish streaming connection after all retries");
    }

    /// <summary>
    /// Processes the streaming response and yields individual chunks.
    /// </summary>
    private async IAsyncEnumerable<string> ProcessStreamingResponse(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        //Util.Log($"[DEBUG] ProcessStreamingResponse started");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        
        //Util.Log($"[DEBUG] Stream reader created, starting to read lines...");
        string? line;
        int lineCount = 0;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            lineCount++;
            if (cancellationToken.IsCancellationRequested)
            {
                //Util.Log($"[DEBUG] Cancellation requested, breaking out of loop");
                yield break;
            }

            //Util.Log($"[DEBUG] Read line #{lineCount}: {(line.Length > 100 ? line.Substring(0, 100) + "..." : line)}");

            // Handle Server-Sent Events format
            if (line.StartsWith("data: "))
            {
                //Util.Log($"[DEBUG] Processing SSE data line");
                var chunk = ProcessStreamingChunk(line);
                if (!string.IsNullOrEmpty(chunk))
                {
                    //Util.Log($"[DEBUG] Extracted chunk with length: {chunk.Length}");
                    yield return chunk;
                }
                else
                {
                    //Util.Log($"[DEBUG] No chunk extracted from data line");
                }
            }
            else
            {
                //Util.Log($"[DEBUG] Skipping non-data line: {line}");
            }
        }
        //Util.Log($"[DEBUG] Stream reading completed, total lines read: {lineCount}");
    }

    /// <summary>
    /// Processes a single streaming chunk and extracts the text content.
    /// </summary>
    /// <param name="data">The raw JSON data from the streaming response.</param>
    /// <returns>The extracted text content from the chunk.</returns>
    private string ProcessStreamingChunk(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            //Util.Log($"[DEBUG] ProcessStreamingChunk: empty or null data");
            return string.Empty;
        }

        try
        {
            // Remove "data: " prefix if present
            string jsonString = data.StartsWith("data: ") ? data.Substring(6) : data;
            //Util.Log($"[DEBUG] Processing JSON chunk: {(jsonString.Length > 200 ? jsonString.Substring(0, 200) + "..." : jsonString)}");
            
            // Use Newtonsoft.Json throughout for consistency
            var jsonData = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);
            if (jsonData == null)
            {
                //Util.Log($"[DEBUG] Failed to deserialize JSON data");
                return string.Empty;
            }

            //Util.Log($"[DEBUG] JSON parsed successfully, protocol: {_config.Protocol}");

            // Handle Gemini streaming response format
            if (_config.Protocol == Protocol.Gemini)
            {
                //Util.Log($"[DEBUG] Processing Gemini format");
                if (jsonData.TryGetValue("candidates", out var candidatesObj) && 
                    candidatesObj is Newtonsoft.Json.Linq.JArray candidates &&
                    candidates.Count > 0)
                {
                    //Util.Log($"[DEBUG] Found candidates array with {candidates.Count} items");
                    var firstCandidate = candidates[0] as Newtonsoft.Json.Linq.JObject;
                    if (firstCandidate != null &&
                        firstCandidate.TryGetValue("content", out var contentToken) &&
                        contentToken is Newtonsoft.Json.Linq.JObject content &&
                        content.TryGetValue("parts", out var partsToken) &&
                        partsToken is Newtonsoft.Json.Linq.JArray parts &&
                        parts.Count > 0)
                    {
                        //Util.Log($"[DEBUG] Found content parts with {parts.Count} items");
                        var firstPart = parts[0] as Newtonsoft.Json.Linq.JObject;
                        if (firstPart != null &&
                            firstPart.TryGetValue("text", out var textToken))
                        {
                            var text = textToken?.ToString() ?? string.Empty;
                            //Util.Log($"[DEBUG] Extracted Gemini text: '{text}'");
                            return text;
                        }
                        else
                        {
                            //Util.Log($"[DEBUG] No 'text' property found in first part");
                        }
                    }
                    else
                    {
                        //Util.Log($"[DEBUG] No content/parts structure found in first candidate");
                    }
                }
                else
                {
                    //Util.Log($"[DEBUG] No candidates array found or empty");
                }
            }
            else
            {
                //Util.Log($"[DEBUG] Processing OpenAI/vLLM format");
                // Standard OpenAI/vLLM streaming format
                if (jsonData.TryGetValue("choices", out var choicesObj) && 
                    choicesObj is Newtonsoft.Json.Linq.JArray choices &&
                    choices.Count > 0)
                {
                    //Util.Log($"[DEBUG] Found choices array with {choices.Count} items");
                    var firstChoice = choices[0] as Newtonsoft.Json.Linq.JObject;
                    
                    if (firstChoice != null)
                    {
                        // Handle completion streaming (prompt-based)
                        if (firstChoice.TryGetValue("text", out var textToken))
                        {
                            var text = textToken?.ToString() ?? string.Empty;
                            //Util.Log($"[DEBUG] Extracted completion text: '{text}'");
                            return text;
                        }
                        
                        // Handle chat completion streaming
                        if (firstChoice.TryGetValue("delta", out var deltaToken) &&
                            deltaToken is Newtonsoft.Json.Linq.JObject delta)
                        {
                            //Util.Log($"[DEBUG] Found delta object");
                            if (delta.TryGetValue("content", out var contentToken))
                            {
                                var text = contentToken?.ToString() ?? string.Empty;
                                //Util.Log($"[DEBUG] Extracted delta content: '{text}'");
                                return text;
                            }
                            
                            // Handle message role changes
                            if (delta.TryGetValue("role", out var roleToken))
                            {
                                //Util.Log($"[DEBUG] Found role change: {roleToken}");
                                // Role changes don't contain content to yield
                                return string.Empty;
                            }
                            
                            //Util.Log($"[DEBUG] Delta object has no content or role property");
                        }
                        else
                        {
                            //Util.Log($"[DEBUG] No text or delta property found in first choice");
                        }
                    }
                }
                else
                {
                    //Util.Log($"[DEBUG] No choices array found or empty");
                }
            }
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            //Util.Log($"[DEBUG] JSON parsing error: {ex.Message}");
            Util.Log($"Warning: Failed to parse streaming JSON chunk: {ex.Message}");
        }

        //Util.Log($"[DEBUG] No content extracted from chunk");
        return string.Empty;
    }
}