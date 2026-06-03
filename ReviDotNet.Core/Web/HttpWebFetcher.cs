// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;
using System.Net;

namespace Revi;

/// <summary>
/// The cheap default fetcher: a single pooled <see cref="HttpClient"/> GET with automatic
/// decompression, redirect following, and a realistic browser-shaped header set. Transient failures
/// (retryable statuses / transport faults) are retried with backoff and <c>Retry-After</c> via
/// <see cref="RetryPolicy"/>. It cannot run JS or pass TLS/JS challenges — it flags likely
/// blocks/challenges via <see cref="FetchResult.Blocked"/> so a tiered escalator (added with
/// <c>ReviDotNet.Scraping</c>) can promote the request to a real browser.
/// </summary>
public sealed class HttpWebFetcher : IWebFetcher
{
    /// <summary>Cheap markers that strongly indicate an interstitial bot challenge rather than content.</summary>
    private static readonly string[] ChallengeMarkers =
    [
        "Just a moment...", "cf-browser-verification", "Checking your browser before accessing",
        "Attention Required! | Cloudflare", "Enable JavaScript and cookies to continue",
        "/cdn-cgi/challenge-platform", "Please verify you are a human", "px-captcha",
    ];

    private static readonly HttpClient Http = CreateClient();

    private readonly IReviLogger<HttpWebFetcher>? _logger;
    private readonly RetryPolicy _retry;
    private readonly BrowserHeaderProfile _profile;

    /// <summary>Creates the fetcher. Logger/retry/header-generator are optional so the tool can build a default pipeline without DI.</summary>
    public HttpWebFetcher(IReviLogger<HttpWebFetcher>? logger = null, RetryPolicy? retry = null, HeaderGenerator? headerGenerator = null)
    {
        _logger = logger;
        _retry = retry ?? RetryPolicy.Default;
        // One coherent header identity, stable for the life of this fetcher (consistent across requests).
        _profile = (headerGenerator ?? new HeaderGenerator()).Generate();
    }

    /// <inheritdoc/>
    public WebFetchTier Tier => WebFetchTier.Http;

    /// <inheritdoc/>
    public async Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, request.Url);
                ApplyHeaders(req, request);

                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(request.TimeoutMs);

                using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                int status = (int)resp.StatusCode;

                if (_retry.IsRetryableStatus(status) && attempt < _retry.MaxRetries)
                {
                    TimeSpan? retryAfter = resp.Headers.RetryAfter?.Delta
                        ?? (resp.Headers.RetryAfter?.Date is DateTimeOffset retryDate ? retryDate - DateTimeOffset.UtcNow : null);
                    _logger?.LogDebug($"HttpWebFetcher: {status} from {request.Url}, retrying (attempt {attempt + 1}/{_retry.MaxRetries}).");
                    await Task.Delay(_retry.GetDelay(attempt + 1, retryAfter), cancellationToken);
                    continue;
                }

                string? contentType = resp.Content.Headers.ContentType?.ToString();
                string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                sw.Stop();

                bool blocked = status is 401 or 403 or 429 || LooksChallenged(body);
                return new FetchResult
                {
                    Html = body,
                    FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? request.Url,
                    StatusCode = status,
                    ContentType = contentType,
                    Tier = Tier,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Blocked = blocked,
                    Note = blocked ? $"http-blocked-or-challenged (status {status})" : null,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // genuine caller cancellation
            }
            catch (Exception ex) when (attempt < _retry.MaxRetries && RetryPolicy.IsRetryableException(ex))
            {
                _logger?.LogDebug($"HttpWebFetcher: transient '{ex.GetType().Name}' fetching {request.Url}, retrying (attempt {attempt + 1}/{_retry.MaxRetries}).");
                await Task.Delay(_retry.GetDelay(attempt + 1), cancellationToken);
                // loop and retry
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger?.LogWarning($"HttpWebFetcher: giving up on {request.Url} after {attempt + 1} attempt(s): {ex.Message}");
                return new FetchResult
                {
                    Html = string.Empty,
                    FinalUrl = request.Url,
                    StatusCode = 0,
                    Tier = Tier,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Blocked = true,
                    Note = $"http-error: {ex.Message}",
                };
            }
        }
    }

    /// <summary>Applies the coherent generated header set (in order), with caller UA/header overrides.</summary>
    private void ApplyHeaders(HttpRequestMessage req, FetchRequest request)
    {
        foreach (KeyValuePair<string, string> h in _profile.Headers)
        {
            if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(request.UserAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
            else
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        if (request.Headers is null) return;
        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            req.Headers.Remove(header.Key);
            req.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    /// <summary>Cheap heuristic: does the body look like a bot-challenge interstitial rather than content?</summary>
    private static bool LooksChallenged(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        string head = body.Length > 4096 ? body[..4096] : body; // markers appear early
        foreach (string marker in ChallengeMarkers)
            if (head.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Builds the shared client with decompression and bounded redirects.</summary>
    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // per-request timeout via CTS
    }
}
