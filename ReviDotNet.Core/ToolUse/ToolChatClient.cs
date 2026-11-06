// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Revi.ToolUse;

/// <summary>How the client authenticates with Anthropic.</summary>
public sealed class ClaudeAuth
{
    private readonly string? _apiKey;
    private readonly Func<CancellationToken, Task<string>>? _bearer;

    private ClaudeAuth(string? apiKey, Func<CancellationToken, Task<string>>? bearer)
    {
        _apiKey = apiKey;
        _bearer = bearer;
    }

    /// <summary>An API key sent as <c>x-api-key</c>.</summary>
    public static ClaudeAuth ApiKey(string apiKey)
        => new(string.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("The key is empty.", nameof(apiKey)) : apiKey, null);

    /// <summary>
    /// A short-lived OAuth access token sent as <c>Authorization: Bearer</c> with the OAuth beta flag,
    /// fetched per request so the caller can refresh it (for example from <c>ant auth print-credentials</c>).
    /// </summary>
    public static ClaudeAuth BearerToken(Func<CancellationToken, Task<string>> tokenProvider)
        => new(null, tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider)));

    /// <summary>The beta flag OAuth tokens require.</summary>
    public const string OAuthBeta = "oauth-2025-04-20";

    internal bool IsBearer => _bearer is not null;

    internal async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_apiKey is not null)
        {
            request.Headers.Remove("x-api-key");
            request.Headers.Add("x-api-key", _apiKey);
            return;
        }
        string token = await _bearer!(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

/// <summary>Options of a <see cref="ToolChatClient"/>.</summary>
public sealed class ToolChatClientOptions
{
    /// <summary>The API base address. Default <c>https://api.anthropic.com/</c>.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.anthropic.com/");

    /// <summary>The provider profile name used when reporting outcomes to <see cref="InferenceProviderMonitor"/>.</summary>
    public string ProviderName { get; init; } = "anthropic";

    /// <summary>How many times a retryable failure is retried. Default 2.</summary>
    public int RetryAttemptLimit { get; init; } = 2;

    /// <summary>The first retry delay; later ones double. Default 1 s.</summary>
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait for response headers, and between streamed events. Default 60 s.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Whether to write request and response dumps through <see cref="Util.DumpLog(string?, string)"/>.</summary>
    public bool DumpLogs { get; init; } = true;
}

/// <summary>
/// A client for tool-capable chat on the Anthropic Messages API: one request in, one answer out,
/// streamed or not. Conversation state and the tool loop live in <see cref="ToolChatSession"/>.
/// </summary>
public sealed class ToolChatClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ClaudeAuth _auth;
    private readonly ToolChatClientOptions _options;

    /// <summary>Creates a client over its own <see cref="HttpClient"/>.</summary>
    public ToolChatClient(ClaudeAuth auth, ToolChatClientOptions? options = null)
        : this(auth, new HttpClient(), ownsHttpClient: true, options) { }

    /// <summary>Creates a client over a caller-owned <see cref="HttpClient"/> (tests, metering handlers).</summary>
    public ToolChatClient(ClaudeAuth auth, HttpClient httpClient, ToolChatClientOptions? options = null)
        : this(auth, httpClient, ownsHttpClient: false, options) { }

    private ToolChatClient(ClaudeAuth auth, HttpClient httpClient, bool ownsHttpClient, ToolChatClientOptions? options)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttp = ownsHttpClient;
        _options = options ?? new ToolChatClientOptions();
        _http.BaseAddress ??= _options.BaseAddress;
        // The watchdog below bounds every wait; the HttpClient timeout must not cut long streams short.
        if (_ownsHttp) _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Disposes the owned HTTP client, if any.</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>Sends a request and waits for the whole answer.</summary>
    public async Task<ToolChatResponse> SendAsync(ToolChatRequest request, CancellationToken cancellationToken = default)
    {
        JObject body = ClaudePayloads.BuildBody(request);
        string json = body.ToString(Formatting.None);
        using HttpResponseMessage response = await SendWithRetryAsync(request, json, streaming: false, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        ToolChatResponse result;
        try
        {
            result = ClaudePayloads.ReadResponse(JObject.Parse(text));
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("The provider returned a response that is not a message: " + Truncate(text), e);
        }
        InferenceProviderMonitor.ReportSuccess(_options.ProviderName, request.Model, streaming: false);
        if (_options.DumpLogs)
            await Util.DumpLog($"### ToolChatClient.SendAsync()\n# Request\n'''\n{Redact(body)}\n'''\n\n# Response\n'''\n{text}\n'''\n", "tc-send");
        return result;
    }

    /// <summary>Sends a request and streams the answer's events; the last event is <see cref="MessageCompletedEvent"/>.</summary>
    public async IAsyncEnumerable<ToolChatEvent> StreamAsync(ToolChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streamingRequest = Clone(request, stream: true);
        JObject body = ClaudePayloads.BuildBody(streamingRequest);
        string json = body.ToString(Formatting.None);
        using HttpResponseMessage response = await SendWithRetryAsync(streamingRequest, json, streaming: true, cancellationToken);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var parser = new ClaudeStreamParser();
        string? eventName = null;
        var data = new StringBuilder();
        var transcript = _options.DumpLogs ? new StringBuilder() : null;

        while (true)
        {
            string? line = await ReadLineWithWatchdogAsync(reader, cancellationToken);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    string payload = data.ToString();
                    transcript?.Append(payload).Append('\n');
                    foreach (ToolChatEvent produced in parser.Feed(eventName, payload))
                        yield return produced;
                    if (parser.Error is not null)
                        throw new InferenceProviderException(_options.ProviderName,
                            InferenceErrorClassifier.Classify(HttpStatusCode.InternalServerError, parser.Error),
                            "The provider reported an error mid-stream: " + parser.Error);
                }
                eventName = null;
                data.Clear();
                if (parser.Completed is not null) break;
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
            // Comments (":") and unknown fields are ignored.
        }

        if (parser.Completed is null)
            throw new InferenceProviderException(_options.ProviderName,
                InferenceErrorClassifier.Classify(HttpStatusCode.BadGateway, "stream ended before message_stop"),
                "The stream ended before the message completed.");

        InferenceProviderMonitor.ReportSuccess(_options.ProviderName, request.Model, streaming: true);
        if (transcript is not null)
            await Util.DumpLog($"### ToolChatClient.StreamAsync()\n# Request\n'''\n{Redact(body)}\n'''\n\n# Events\n'''\n{transcript}\n'''\n", "tc-stream");
    }

    private async Task<string?> ReadLineWithWatchdogAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        Task<string?> readTask = reader.ReadLineAsync(cancellationToken).AsTask();
        Task delay = Task.Delay(_options.InactivityTimeout, cancellationToken);
        Task done = await Task.WhenAny(readTask, delay);
        if (done == delay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"No streamed data for {_options.InactivityTimeout.TotalSeconds:0}s.");
        }
        return await readTask;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(ToolChatRequest request, string json, bool streaming, CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                httpRequest.Headers.Add("anthropic-version", ClaudePayloads.ApiVersion);
                var betas = new List<string>(ClaudePayloads.Betas(request));
                if (_auth.IsBearer && !betas.Contains(ClaudeAuth.OAuthBeta)) betas.Add(ClaudeAuth.OAuthBeta);
                if (betas.Count > 0) httpRequest.Headers.Add("anthropic-beta", string.Join(",", betas));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? "text/event-stream" : "application/json"));
                await _auth.ApplyAsync(httpRequest, cancellationToken);

                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<HttpResponseMessage> sendTask = _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, sendCts.Token);
                Task delay = Task.Delay(_options.InactivityTimeout, cancellationToken);
                if (await Task.WhenAny(sendTask, delay) == delay)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sendCts.Cancel();
                    throw new TimeoutException($"No response headers within {_options.InactivityTimeout.TotalSeconds:0}s.");
                }
                response = await sendTask;
                if (response.IsSuccessStatusCode) return response;

                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                InferenceFailure failure = InferenceErrorClassifier.Classify(response.StatusCode, errorBody);
                if (!failure.IsRetryable || attempt >= _options.RetryAttemptLimit)
                {
                    string message = $"Anthropic request failed: {(int)response.StatusCode} {response.ReasonPhrase} ({failure}): {Truncate(errorBody)}";
                    Util.Log(message);
                    InferenceProviderMonitor.ReportFailure(_options.ProviderName, request.Model, failure, streaming);
                    response.Dispose();
                    throw new InferenceProviderException(_options.ProviderName, failure, message);
                }
                TimeSpan wait = RetryDelay(attempt, response);
                Util.Log($"Anthropic request got {(int)response.StatusCode} ({failure}); retrying in {wait.TotalSeconds:0.#}s.");
                response.Dispose();
                await Task.Delay(wait, cancellationToken);
                attempt++;
            }
            catch (InferenceProviderException) { throw; }
            catch (OperationCanceledException) { response?.Dispose(); throw; }
            catch (Exception e) when (e is HttpRequestException or TimeoutException)
            {
                response?.Dispose();
                if (attempt >= _options.RetryAttemptLimit) throw;
                TimeSpan wait = TimeSpan.FromTicks(_options.RetryInitialDelay.Ticks * (1L << attempt));
                Util.Log($"Anthropic request failed to connect ({e.Message}); retrying in {wait.TotalSeconds:0.#}s.");
                await Task.Delay(wait, cancellationToken);
                attempt++;
            }
        }
    }

    private TimeSpan RetryDelay(int attempt, HttpResponseMessage response)
    {
        TimeSpan backoff = TimeSpan.FromTicks(_options.RetryInitialDelay.Ticks * (1L << attempt));
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        TimeSpan? advised = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return advised is { } a && a > TimeSpan.Zero && a <= TimeSpan.FromMinutes(2) && a > backoff ? a : backoff;
    }

    private static ToolChatRequest Clone(ToolChatRequest r, bool stream) => new()
    {
        Model = r.Model, System = r.System, CacheSystem = r.CacheSystem, Messages = r.Messages, Tools = r.Tools,
        CacheTools = r.CacheTools, Thinking = r.Thinking, Effort = r.Effort, ShowThinking = r.ShowThinking,
        MaxTokens = r.MaxTokens, StopSequences = r.StopSequences, ToolChoice = r.ToolChoice,
        DisableParallelToolUse = r.DisableParallelToolUse, OutputSchema = r.OutputSchema, Betas = r.Betas,
        UseRefusalFallbacks = r.UseRefusalFallbacks, UserId = r.UserId, Stream = stream
    };

    private static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000] + "…";

    private static string Redact(JObject body)
    {
        // Images are large and may be private; replace their data in dumps.
        var copy = (JObject)body.DeepClone();
        foreach (JToken source in copy.SelectTokens("$..source").ToList())
            if (source is JObject s && s["data"] is not null) s["data"] = "<base64 omitted>";
        return copy.ToString(Formatting.Indented);
    }
}
