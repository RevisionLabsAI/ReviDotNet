// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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
        CancellationToken cancellationToken,
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
            return await MakeRequestAsync(endpoint, body, cancellationToken, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds);
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
        string body,
        CancellationToken cancellationToken,
        int? inactivityTimeoutSeconds = null)
    {
        int attempt = 0;
        HttpResponseMessage? response = null;
        TimeSpan inactivity = TimeSpan.FromSeconds(Math.Max(1, inactivityTimeoutSeconds ?? _config.InactivityTimeoutSeconds));
        string uri = (_httpClient.BaseAddress?.ToString() ?? string.Empty) + endpoint;

        while (true)
        {
            HttpRequestMessage? request = null;
            try
            {
                // Build request and content fresh each attempt to avoid disposed content reuse
                request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                };

                using CancellationTokenSource sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<HttpResponseMessage> sendTask = _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendCts.Token);
                Task delayTask = Task.Delay(inactivity, cancellationToken);
                Task completed = await Task.WhenAny(sendTask, delayTask);
                if (completed == delayTask)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    sendCts.Cancel();
                    throw new TimeoutException($"[{attempt + 1}/{_config.RetryAttemptLimit}] Did not receive response headers from '{uri}' within {inactivity.TotalSeconds}s.");
                }
                response = await sendTask;

                if (!response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (attempt >= _config.RetryAttemptLimit)
                    {
                        string msg = $"[{attempt + 1}] API request failed: {response.ReasonPhrase} ({(int)response.StatusCode}) from '{uri}'. Body: '{responseContent}'";
                        Util.Log(msg);
                        await Util.DumpLog(msg + $"\nResponse:\n'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''\n", "ic-api-failure");
                        throw new Exception(msg);
                    }

                    double delaySeconds = _config.RetryInitialDelaySeconds * Math.Pow(2, attempt);
                    string retryMsg = $"[{attempt + 1}] Non-success response from '{uri}', retrying in {delaySeconds}s: {response.ReasonPhrase} ({(int)response.StatusCode})";
                    Util.Log(retryMsg);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    attempt++;
                    response.Dispose();
                    continue; // retry
                }

                // Success -> process body without inactivity watchdog (slow bodies are allowed)
                        Dictionary<string, string> result = await ProcessHttpResponseAsync(response, cancellationToken);
                response.Dispose();
                return result;
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                request?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TimeoutException)
            {
                response?.Dispose();
                request?.Dispose();

                if (attempt >= _config.RetryAttemptLimit)
                {
                    string msg = $"[{attempt + 1}] Request to '{uri}' failed: {ex.Message}";
                    Util.Log(msg);
                    throw;
                }
                double delaySeconds = _config.RetryInitialDelaySeconds * Math.Pow(2, attempt);
                Util.Log($"[{attempt + 1}] Transient exception contacting '{uri}', retrying in {delaySeconds}s: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                attempt++;
                continue;
            }
            finally
            {
                // Do not dispose response on success until content has been read above
            }
        }
    }

    /// <summary>
    /// Processes the HTTP response and extracts the required information.
    /// </summary>
    /// <param name="response">The HTTP response returned by the server.</param>
    /// <returns>A dictionary containing the extracted information from the response.</returns>
    private async Task<Dictionary<string, string>> ProcessHttpResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Dictionary<string, JsonElement>? data = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(cancellationToken: cancellationToken);
        
        if (data == null)
            throw new Exception($"ProcessHttpResponse: Invalid response (data null):\n'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''\n");

        Dictionary<string, string> result = new Dictionary<string, string>();

        // Handle Anthropic Claude response format
        if (_config.Protocol == Protocol.Claude)
        {
            // Anthropic messages response: { content:[{type:"text", text:"..."}], stop_reason, usage:{input_tokens, output_tokens} }
            if (data.TryGetValue("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentElem.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text")
                    {
                        if (item.TryGetProperty("text", out var textEl))
                        {
                            result["text"] = textEl.GetString() ?? string.Empty;
                            break;
                        }
                    }
                }
            }
            if (data.TryGetValue("stop_reason", out var stopReason))
                result["finish_reason"] = stopReason.GetString() ?? string.Empty;
            if (data.TryGetValue("usage", out var claudeUsage) && claudeUsage.ValueKind == JsonValueKind.Object)
            {
                if (claudeUsage.TryGetProperty("input_tokens", out var it)) result["input_tokens"] = it.GetInt32().ToString();
                if (claudeUsage.TryGetProperty("output_tokens", out var ot)) result["output_tokens"] = ot.GetInt32().ToString();
            }
            return result;
        }

        // Handle Gemini response format
        if (_config.Protocol == Protocol.Gemini)
        {
            if (data.TryGetValue("candidates", out JsonElement candidates) &&
                candidates.ValueKind == JsonValueKind.Array)
            {
                JsonElement firstCandidate = candidates[0];
                if (firstCandidate.TryGetProperty("content", out JsonElement content) &&
                    content.TryGetProperty("parts", out JsonElement parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    JsonElement firstPart = parts[0];
                    if (firstPart.TryGetProperty("text", out JsonElement textElement))
                        result.Add("text", textElement.GetString() ?? "");
                }
                if (firstCandidate.TryGetProperty("finishReason", out JsonElement finishReason))
                    result.Add("finish_reason", finishReason.GetString() ?? string.Empty);
            }
            if (data.TryGetValue("usageMetadata", out var geminiMeta) && geminiMeta.ValueKind == JsonValueKind.Object)
            {
                if (geminiMeta.TryGetProperty("promptTokenCount", out var pt)) result["input_tokens"] = pt.GetInt32().ToString();
                if (geminiMeta.TryGetProperty("candidatesTokenCount", out var ct)) result["output_tokens"] = ct.GetInt32().ToString();
            }
        }
        else
        {
            // OpenAI: support both Chat/Completions and new Responses API
            // First, try Responses API shape
            if (data.TryGetValue("output_text", out JsonElement outputText))
            {
                result["text"] = outputText.GetString() ?? string.Empty;
                if (data.TryGetValue("status", out JsonElement statusEl))
                    result["finish_reason"] = statusEl.GetString() ?? string.Empty;
                if (data.TryGetValue("usage", out var respUsage) && respUsage.ValueKind == JsonValueKind.Object)
                {
                    if (respUsage.TryGetProperty("input_tokens", out var it)) result["input_tokens"] = it.GetInt32().ToString();
                    if (respUsage.TryGetProperty("output_tokens", out var ot)) result["output_tokens"] = ot.GetInt32().ToString();
                }
                return result;
            }

            // Fallback to legacy choices-based parsing (Chat/Completions or vLLM-compatible)
            if (!data.TryGetValue("choices", out JsonElement choices))
                throw new Exception($"ProcessHttpResponse: Invalid response (missing choices and not Responses shape):\n'''\n{JsonConvert.SerializeObject(response, Formatting.Indented)}\n'''\n");

            if (choices[0].TryGetProperty("text", out JsonElement textElement))
            {
                result.Add("text", textElement.GetString() ?? "");
            }
            else
            {
                // Extracting message content from the chat completion response
                if (choices[0].TryGetProperty("message", out JsonElement messageElement) &&
                    messageElement.TryGetProperty("content", out JsonElement contentElement))
                {
                    result.Add("text", contentElement.GetString() ?? "");
                }
            }
            if (choices[0].TryGetProperty("finish_reason", out JsonElement finishReason))
                result.Add("finish_reason", finishReason.GetString() ?? string.Empty);

            // OpenAI/vLLM usage: { prompt_tokens, completion_tokens }
            if (data.TryGetValue("usage", out var oaiUsage) && oaiUsage.ValueKind == JsonValueKind.Object)
            {
                if (oaiUsage.TryGetProperty("prompt_tokens", out var pt)) result["input_tokens"] = pt.GetInt32().ToString();
                if (oaiUsage.TryGetProperty("completion_tokens", out var ct)) result["output_tokens"] = ct.GetInt32().ToString();
            }
        }

        return result;
    }
}