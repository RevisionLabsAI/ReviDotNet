using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Revi;

/// <summary>HTTP adapter for the SystemOne wire protocol, independent of provider hostname or model family.</summary>
public sealed class SystemOneClient : IDecisionModelClient, IDisposable
{
    /// <summary>Longest wait <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts.</summary>
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ProviderProfile _provider;
    private readonly SemaphoreSlim _gate;

    // Disposal waits for in-flight evaluations: a provider reload disposes the old profile's client while
    // requests may still hold it, and releasing a disposed gate would turn their answers into exceptions.
    private int _active;
    private int _disposed;
    private int _released;

    /// <summary>Creates an adapter. Inject an HTTP client for fixtures or a custom handler pipeline.</summary>
    public SystemOneClient(ProviderProfile provider, HttpClient? httpClient = null)
    {
        _provider = provider;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = httpClient is null;
        _gate = new SemaphoreSlim(Math.Max(1, provider.SimultaneousRequests ?? 10));
    }

    /// <summary>The profile whose URL, key and limits this client sends; a copied profile must not reuse it.</summary>
    internal ProviderProfile Provider => _provider;

    /// <inheritdoc/>
    public async Task<DecisionRun> EvaluateAsync(DecisionRequest request, DecisionOptions? options = null, CancellationToken token = default)
    {
        DecisionValidation.Validate(request);
        int retries = options?.RetryLimit ?? _provider.RetryAttemptLimit ?? 2;
        if (retries < 0 || retries > 10) throw new ArgumentOutOfRangeException(nameof(options), "Retry limit must be 0–10.");
        TimeSpan timeout = options?.Timeout ?? TimeSpan.FromSeconds(_provider.TimeoutSeconds ?? 10);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        Stopwatch clock = Stopwatch.StartNew();
        CancellationToken ct = deadline.Token;
        JsonObject questions = [];
        foreach (KeyValuePair<string, DecisionQuestion> pair in request.Questions)
        {
            JsonObject question = new() { ["type"] = pair.Value.Kind, ["instructions"] = JsonSerializer.SerializeToNode(pair.Value.Instructions) };
            if (pair.Value.Criteria is not null) question["criteria"] = JsonSerializer.SerializeToNode(pair.Value.Criteria);
            questions.Add(pair.Key, question);
        }
        string body = new JsonObject { ["model"] = request.Model, ["state"] = JsonSerializer.SerializeToNode(request.State), ["questions"] = questions }.ToJsonString();
        string version = _provider.APIVersionPath ?? "v1";
        string path = version.Equals("none", StringComparison.OrdinalIgnoreCase) ? "systemone" : version.Trim('/') + "/systemone";
        Uri endpoint = new(new Uri((_provider.APIURL ?? throw new InvalidOperationException("Decision provider has no API URL.")).TrimEnd('/') + "/"), path);
        Interlocked.Increment(ref _active);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return await SendWithRetriesAsync(endpoint, body, request, retries, timeout, clock, ct).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _disposed) == 1) ReleaseResources();
        }
    }

    /// <summary>Sends under the concurrency gate with bounded 429/529 retries; only called while counted as active.</summary>
    private async Task<DecisionRun> SendWithRetriesAsync(Uri endpoint, string body, DecisionRequest request, int retries, TimeSpan timeout, Stopwatch clock, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using HttpRequestMessage message = new(HttpMethod.Post, endpoint);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.APIKey ?? "");
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if ((status == 429 || status == 529) && attempt < retries)
                {
                    TimeSpan delay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow :
                            TimeSpan.FromSeconds(Math.Max(1, _provider.RetryInitialDelaySeconds ?? 1) * Math.Pow(2, attempt)));
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    // A wait that outlasts the deadline (or Task.Delay's own limit) could only end as a timeout:
                    // report the provider's status instead, still without its body.
                    if (delay > MaximumRetryDelay || (timeout != Timeout.InfiniteTimeSpan && delay >= timeout - clock.Elapsed))
                        throw new HttpRequestException($"SystemOne request failed with HTTP {status}.", null, response.StatusCode);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"SystemOne request failed with HTTP {status}.", null, response.StatusCode);
                // Never include response text in an exception: error bodies can echo private state.
                try
                {
                    using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    JsonNode? root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    return ParseResponse(root as JsonObject ?? throw new FormatException(), request);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
                {
                    throw new InvalidDataException("SystemOne returned an invalid or incomplete decision response.");
                }
            }
        }
        finally
        {
            // Spacing must not discard an already received billable answer or mask the original error.
            try { if ((_provider.DelayBetweenRequestsMs ?? 0) > 0) await Task.Delay(_provider.DelayBetweenRequestsMs!.Value, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Parses and validates the entire answer set before any caller can act on it.</summary>
    private static DecisionRun ParseResponse(JsonObject root, DecisionRequest request)
    {
        string model = root["model"]?.GetValue<string>() ?? throw new FormatException();
        if (string.IsNullOrWhiteSpace(model)) throw new FormatException();
        JsonObject answers = root["answers"] as JsonObject ?? throw new FormatException();
        if (answers.Count != request.Questions.Count || answers.Any(a => !request.Questions.ContainsKey(a.Key))) throw new FormatException();
        Dictionary<string, DecisionAnswer> result = [];
        foreach (KeyValuePair<string, DecisionQuestion> pair in request.Questions)
        {
            JsonObject answer = answers[pair.Key] as JsonObject ?? throw new FormatException();
            if (answer["type"]?.GetValue<string>() != pair.Value.Kind) throw new FormatException();
            if (pair.Value is BooleanQuestion)
                result.Add(pair.Key, new NoulAnswer(Probability(answer["noul"])));
            else
            {
                JsonObject raw = answer["probabilities"] as JsonObject ?? throw new FormatException();
                Dictionary<string, double> probabilities = raw.ToDictionary(p => p.Key, p => Probability(p.Value), StringComparer.Ordinal);
                if (Math.Abs(probabilities.Values.Sum() - 1) > 0.001) throw new FormatException();
                double confidence = Probability(answer["confidence"]);
                ReadOnlyDictionary<string, double> distribution = new(probabilities);
                if (pair.Value is ChoiceQuestion choice)
                {
                    string selected = answer["choice"]?.GetValue<string>() ?? throw new FormatException();
                    if (probabilities.Count != choice.Options.Count || choice.Options.Keys.Any(k => !probabilities.ContainsKey(k)) ||
                        !probabilities.TryGetValue(selected, out double selectedProbability) || selectedProbability + 0.000001 < probabilities.Values.Max()) throw new FormatException();
                    result.Add(pair.Key, new ChoiceAnswer(selected, confidence, distribution));
                }
                else if (pair.Value is ScoreQuestion score)
                {
                    string[] indices = Enumerable.Range(0, score.Levels.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
                    JsonObject legend = answer["legend"] as JsonObject ?? throw new FormatException();
                    if (probabilities.Count != indices.Length || legend.Count != indices.Length || indices.Any(i => !probabilities.ContainsKey(i) || !legend.ContainsKey(i))) throw new FormatException();
                    double value = answer["score"]?.GetValue<double>() ?? throw new FormatException();
                    double expected = indices.Sum(i => int.Parse(i, CultureInfo.InvariantCulture) * probabilities[i]);
                    if (!double.IsFinite(value) || Math.Abs(value - expected) > 0.01) throw new FormatException();
                    result.Add(pair.Key, new ScoreAnswer(value, confidence, distribution,
                        new ReadOnlyDictionary<string, string>(legend.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? throw new FormatException()))));
                }
            }
        }
        long input = root["usage"]?["input_tokens"]?.GetValue<long>() ?? throw new FormatException();
        long output = root["usage"]?["output_tokens"]?.GetValue<long>() ?? throw new FormatException();
        if (input < 0 || output < 0) throw new FormatException();
        return new DecisionRun(model, new ReadOnlyDictionary<string, DecisionAnswer>(result), new DecisionUsage(input, output));
    }

    /// <summary>Accepts finite values in the probability interval only.</summary>
    private static double Probability(JsonNode? node)
    {
        double value = node?.GetValue<double>() ?? throw new FormatException();
        return double.IsFinite(value) && value is >= 0 and <= 1 ? value : throw new FormatException();
    }

    /// <summary>Stops new evaluations; the HTTP client and gate are released once in-flight evaluations finish.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (Volatile.Read(ref _active) == 0) ReleaseResources();
    }

    /// <summary>Releases the owned HTTP client and the gate exactly once.</summary>
    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) return;
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }
}
