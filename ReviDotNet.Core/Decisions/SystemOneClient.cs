using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Revi;

/// <summary>HTTP adapter for the SystemOne wire protocol, independent of provider hostname or model family.</summary>
public sealed class SystemOneClient : IDecisionModelClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ProviderProfile _provider;
    private readonly SemaphoreSlim _gate;

    /// <summary>Creates an adapter. Inject an HTTP client for fixtures or a custom handler pipeline.</summary>
    public SystemOneClient(ProviderProfile provider, HttpClient? httpClient = null)
    {
        _provider = provider;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = httpClient is null;
        _gate = new SemaphoreSlim(Math.Max(1, provider.SimultaneousRequests ?? 10));
    }

    /// <inheritdoc/>
    public async Task<DecisionRun> EvaluateAsync(DecisionRequest request, DecisionOptions? options = null, CancellationToken token = default)
    {
        DecisionValidation.Validate(request);
        int retries = options?.RetryLimit ?? _provider.RetryAttemptLimit ?? 2;
        if (retries < 0 || retries > 10) throw new ArgumentOutOfRangeException(nameof(options), "Retry limit must be 0–10.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options?.Timeout ?? TimeSpan.FromSeconds(_provider.TimeoutSeconds ?? 10));
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
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct).ConfigureAwait(false);
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

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }
}
