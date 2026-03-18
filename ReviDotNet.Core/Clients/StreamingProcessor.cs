// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;

namespace Revi;

/// <summary>
/// Handles streaming requests to providers and emits parsed text chunks while tracking metadata.
/// Adds optional debug logging for partial responses when enabled via InferClientConfig.DebugStreaming.
/// </summary>
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

    /// <summary>
    /// Redacts sensitive query parameters (e.g., API keys) in a URL string.
    /// </summary>
    /// <param name="url">The URL which may contain sensitive parameters.</param>
    /// <returns>A redacted URL safe for logging.</returns>
    private static string RedactUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        try
        {
            Uri uri;
            bool parsed = Uri.TryCreate(url, UriKind.Absolute, out uri);
            if (!parsed)
            {
                // Try relative to ensure we can still scrub query
                parsed = Uri.TryCreate("http://localhost" + (url.StartsWith("/") ? string.Empty : "/") + url, UriKind.Absolute, out uri);
            }

            if (!parsed || uri == null)
            {
                return url;
            }

            string query = uri.Query;
            if (string.IsNullOrEmpty(query))
            {
                return url;
            }

            // Very small scrubber: replace key=VALUE with key=****
            string redacted = query.Replace("key=", "key=****");
            string result = url.Replace(query, redacted);
            return result;
        }
        catch
        {
            return url;
        }
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
        CancellationToken cancellationToken,
        Func<StreamingMetadata, string, Task>? onCompletionCallback = null,
        Func<Exception, Task>? onErrorCallback = null)
    {
        DateTime startTime = DateTime.UtcNow;
        StreamingMetadataTracker metadataTracker = new StreamingMetadataTracker(startTime);
        StringBuilder responseBuilder = new StringBuilder();
    
        IAsyncEnumerable<string> trackedStream = TrackStreamingMetadata(sourceStream, metadataTracker, cancellationToken, responseBuilder);
        
        // Set up completion task to handle callbacks when streaming finishes
        Task<StreamingMetadata> completionTask = HandleStreamingCompletion(metadataTracker.CompletionTask, responseBuilder, onCompletionCallback, onErrorCallback);
        
        // IMPORTANT: Ensure the completion task runs even if no one awaits it
        // This fire-and-forget approach ensures callbacks are executed
        _ = completionTask.ContinueWith(t => 
        {
            if (t.IsFaulted)
            {
                Util.Log($"StreamingProcessor completion task faulted: {t.Exception?.GetBaseException().Message}");
            }
            else if (t.IsCanceled)
            {
                Util.Log("StreamingProcessor completion task was canceled");
            }
            // Success case is handled by the callbacks themselves
        }, TaskScheduler.Default);
    
        return new StreamingResult<string>
        {
            Stream = trackedStream,
            Completion = completionTask
        };
    }

    /// <summary>
    /// Handles streaming completion callbacks without affecting the stream itself.
    /// </summary>
    private async Task<StreamingMetadata> HandleStreamingCompletion(
        Task<StreamingMetadata> originalCompletion,
        StringBuilder responseBuilder,
        Func<StreamingMetadata, string, Task>? onCompletionCallback,
        Func<Exception, Task>? onErrorCallback)
    {
        try
        {
            StreamingMetadata metadata = await originalCompletion;
            string fullResponse = responseBuilder.ToString();
            
            // Call completion callback for any completion that has response data (including cancellations)
            if (onCompletionCallback != null && !string.IsNullOrEmpty(fullResponse))
            {
                try
                {
                    //Util.Log("OnCompletionCallback!");
                    await onCompletionCallback(metadata, fullResponse);
                }
                catch (Exception ex)
                {
                    Util.Log($"Error in streaming completion callback: {ex.Message}");
                }
            }
            // Call error callback only for actual errors that don't have response data
            else if (!metadata.IsSuccess && metadata.Exception != null && onErrorCallback != null &&
                     !(metadata.Exception is OperationCanceledException) && string.IsNullOrEmpty(fullResponse))
            {
                try
                {
                    //Util.Log("OnErrorCallback!");
                    await onErrorCallback(metadata.Exception);
                }
                catch (Exception ex)
                {
                    Util.Log($"Error in streaming error callback: {ex.Message}");
                }
            }
            
            return metadata;
        }
        catch (Exception ex)
        {
            // If the completion task itself fails, call error callback
            if (onErrorCallback != null && !(ex is OperationCanceledException))
            {
                try
                {
                    //Util.Log("OnErrorCallback!");
                    await onErrorCallback(ex);
                }
                catch (Exception callbackEx)
                {
                    Util.Log($"Error in streaming error callback: {callbackEx.Message}");
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Tracks streaming metadata without buffering the stream.
    /// </summary>
    private async IAsyncEnumerable<string> TrackStreamingMetadata(
        IAsyncEnumerable<string> sourceStream,
        StreamingMetadataTracker tracker,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        StringBuilder responseBuilder)
    {
        IAsyncEnumerator<string> enumerator = sourceStream.GetAsyncEnumerator(cancellationToken);
        bool completedSuccessfully = false;
        
        try
        {
            while (await MoveNextSafely(enumerator, tracker))
            {
                string chunk = enumerator.Current;
                responseBuilder.Append(chunk); // Collect chunks as they're yielded
                yield return chunk;
            }
            completedSuccessfully = true;
            tracker.CompleteSuccessfully();
        }
        finally
        {
            // If we didn't complete successfully and the tracker hasn't been completed yet,
            // it means we exited due to an exception or cancellation
            if (!completedSuccessfully && !tracker.CompletionTask.IsCompleted)
            {
                // Check if it was cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    tracker.CompleteCanceled(new OperationCanceledException(cancellationToken));
                }
                else
                {
                    // Some other reason - complete with a generic error
                    tracker.CompleteWithError(new Exception("Stream ended unexpectedly"));
                }
            }
            
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
            bool hasNext = await enumerator.MoveNextAsync();
            if (hasNext)
            {
                tracker.IncrementChunkCount();
            }
            return hasNext;
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
        {
            tracker.CompleteCanceled(ex);
            return false; // Return false instead of throwing to end the stream gracefully
        }
        catch (System.Net.Http.HttpIOException ex)
        {
            tracker.CompleteWithError(ex);
            return false; // Return false instead of throwing to end the stream gracefully
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
        [EnumeratorCancellation] CancellationToken cancellationToken,
        int? inactivityTimeoutSeconds = null)
    {
        await _clientSemaphore.WaitAsync(cancellationToken);
        try
        {
            await _rateLimiter.EnsureRateLimit();
            
            // Handle protocol-specific payload transformation
            if (_config.Protocol == Protocol.Gemini)
            {
                payload = _payloadTransformer.TransformToGeminiPayload(payload);
                // Add API key to endpoint for Gemini
                if (_config.UseApiKey && !endpoint.Contains("key="))
                {
                    endpoint += (endpoint.Contains("?") ? "&" : "?") + $"key={_config.ApiKey}";
                }
            }
            else if (_config.Protocol == Protocol.Claude)
            {
                payload = _payloadTransformer.TransformToClaudePayload(payload);
            }
            
            string body = JsonConvert.SerializeObject(payload);
            int chunkCount = 0;
            await foreach (string chunk in MakeStreamingRequestAsync(endpoint, body, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds))
            {
                chunkCount++;
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
        string body,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        int? inactivityTimeoutSeconds = null)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await EstablishStreamingConnection(endpoint, body, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds);
            
            await foreach (string chunk in ProcessStreamingResponse(response, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds))
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
        string body,
        CancellationToken cancellationToken,
        int inactivityTimeoutSeconds)
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
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                // Ensure we negotiate for SSE
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
                // ... existing code ...
                // Wait for response headers with inactivity watchdog
                TimeSpan inactivity = TimeSpan.FromSeconds(Math.Max(1, inactivityTimeoutSeconds));
                using CancellationTokenSource sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<HttpResponseMessage> sendTask = _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    sendCts.Token);
                Task delayTask = Task.Delay(inactivity, cancellationToken);
                Task completed = await Task.WhenAny(sendTask, delayTask);
                if (completed == delayTask)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    sendCts.Cancel();
                    string uri = (_httpClient.BaseAddress?.ToString() ?? string.Empty) + endpoint;
                    throw new TimeoutException($"[{retryAttempt + 1}/{_config.RetryAttemptLimit}] Did not receive streaming response headers from '{uri}' within {inactivity.TotalSeconds}s.");
                }
                response = await sendTask;

                //Util.Log($"[DEBUG] HTTP response received - Status: {response.StatusCode} ({response.ReasonPhrase})");
                
                if (response.IsSuccessStatusCode)
                {
                    //Util.Log($"[DEBUG] Connection established successfully");
                    return response;
                }
                
                // Handle non-success status codes
                string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Util.Log($"EstablishStreamingConnection: Non-success response content: {responseContent}");
                
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
        Util.Log($"EstablishStreamingConnection: Failed to establish connection after all retries");
        throw new Exception("Failed to establish streaming connection after all retries");
    }

    /// <summary>
    /// Processes the streaming response and yields individual chunks.
    /// </summary>
    private async IAsyncEnumerable<string> ProcessStreamingResponse(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        int inactivityTimeoutSeconds)
    {
        //Util.Log($"[DEBUG] ProcessStreamingResponse started");
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new StreamReader(stream);
        
        //Util.Log($"[DEBUG] Stream reader created, starting to read lines...");
        string? line;
        int lineCount = 0;
        TimeSpan inactivity = TimeSpan.FromSeconds(inactivityTimeoutSeconds);
        while (true)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                Task<string?> readTask = reader.ReadLineAsync(cancellationToken).AsTask();
                Task delayTask = Task.Delay(inactivity, cancellationToken);
                Task completed = await Task.WhenAny(readTask, delayTask);
                if (completed == delayTask)
                {
                    if (cancellationToken.IsCancellationRequested)
                        yield break;
                    string uri = response.RequestMessage?.RequestUri?.ToString() ?? "(unknown)";
                    throw new TimeoutException($"No streaming data received from '{RedactUrl(uri)}' for {inactivity.TotalSeconds} seconds.");
                }

                line = await readTask;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is System.Net.Http.HttpIOException)
            {
                // Gracefully handle cancellation or premature connection closure
                yield break;
            }

            if (line is null)
                yield break;
            
            lineCount++;

            string trimmed = line.Trim();

            // Skip keep-alives or empty event lines (valid in SSE)
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(":"))
            {
                // keep-alive/comment, ignore
                continue;
            }

            // Explicitly handle OpenAI end-of-stream sentinel (allow incidental whitespace)
            if (trimmed.StartsWith("data: [DONE]"))
            {
                //Util.Log($"[DEBUG] Received [DONE] sentinel, ending stream");
                yield break;
            }

            // Handle Server-Sent Events format
            if (trimmed.StartsWith("data: "))
            {
                //Util.Log($"[DEBUG] Processing SSE data line");
                string chunk = ProcessStreamingChunk(trimmed);
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
            jsonString = jsonString.Trim();

            // Guard for sentinel or empty payloads arriving here
            if (jsonString.Length == 0 || string.Equals(jsonString, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // Use Newtonsoft.Json throughout for consistency
            var jsonData = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);
            if (jsonData == null)
            {
                //Util.Log($"[DEBUG] Failed to deserialize JSON data");
                return string.Empty;
            }

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
            else if (_config.Protocol == Protocol.Claude)
            {
                // Anthropic SSE format. Typical events: content_block_delta with {"delta":{"type":"text_delta","text":"..."}}
                if (jsonData.TryGetValue("type", out var typeObj) && typeObj is string typeStr)
                {
                    if (typeStr == "content_block_delta")
                    {
                        if (jsonData.TryGetValue("delta", out var deltaObj) && deltaObj is Newtonsoft.Json.Linq.JObject delta &&
                            delta.TryGetValue("type", out var deltaTypeToken) && deltaTypeToken?.ToString() == "text_delta" &&
                            delta.TryGetValue("text", out var textToken) && textToken != null)
                        {
                            return textToken.ToString();
                        }
                    }
                    else if (typeStr == "message_delta")
                    {
                        // message_delta may contain stop_reason; nothing to yield
                        return string.Empty;
                    }
                }
            }
            else
            {
                // OpenAI/vLLM format
                if (jsonData.TryGetValue("choices", out var choicesObj) && 
                    choicesObj is Newtonsoft.Json.Linq.JArray choices &&
                    choices.Count > 0)
                {
                    var firstChoice = choices[0] as Newtonsoft.Json.Linq.JObject;
                    if (firstChoice != null)
                    {
                        // Completion (legacy / text) streaming
                        if (firstChoice.TryGetValue("text", out var textToken))
                        {
                            var text = textToken?.ToString() ?? string.Empty;
                            return text;
                        }

                        // Chat streaming
                        if (firstChoice.TryGetValue("delta", out var deltaToken) &&
                            deltaToken is Newtonsoft.Json.Linq.JObject delta)
                        {
                            // 1) Regular assistant content
                            if (delta.TryGetValue("content", out var contentToken) && contentToken != null)
                            {
                                var text = contentToken.ToString();
                                return text;
                            }

                            // 2) Tool/function call streaming arguments
                            //    OpenAI: choices[].delta.tool_calls[i].function.arguments (streamed incrementally)
                            if (delta.TryGetValue("tool_calls", out var toolCallsToken) &&
                                toolCallsToken is Newtonsoft.Json.Linq.JArray toolCalls &&
                                toolCalls.Count > 0)
                            {
                                var sb = new System.Text.StringBuilder();
                                foreach (var tc in toolCalls)
                                {
                                    if (tc is Newtonsoft.Json.Linq.JObject tcObj &&
                                        tcObj.TryGetValue("function", out var fnToken) &&
                                        fnToken is Newtonsoft.Json.Linq.JObject fnObj &&
                                        fnObj.TryGetValue("arguments", out var argsToken) &&
                                        argsToken != null)
                                    {
                                        sb.Append(argsToken.ToString());
                                    }
                                }
                                return sb.ToString();
                            }

                            // 3) Legacy function_call (older models)
                            if (delta.TryGetValue("function_call", out var fnCallToken) &&
                                fnCallToken is Newtonsoft.Json.Linq.JObject fnCallObj &&
                                fnCallObj.TryGetValue("arguments", out var fnArgsToken) &&
                                fnArgsToken != null)
                            {
                                return fnArgsToken.ToString();
                            }

                            // Role changes carry no content to yield
                            if (delta.TryGetValue("role", out _))
                            {
                                return string.Empty;
                            }
                        }
                    }
                }
            }
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            //Util.Log($"[DEBUG] JSON parsing error: {ex.Message}");
            Util.Log($"Warning: Failed to parse streaming JSON chunk: {ex.Message}");
        }

        return string.Empty;
    }
}