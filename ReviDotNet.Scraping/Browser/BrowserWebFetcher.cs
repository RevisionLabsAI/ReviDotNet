// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;
using PuppeteerSharp;

namespace Revi;

/// <summary>
/// Browser-tier <see cref="IWebFetcher"/>: drives a real Chromium (via the shared
/// <see cref="IBrowserService"/> pool) to render JS and produce an authentic TLS/HTTP-2 + JS
/// fingerprint — a strict superset of any HTTP-level impersonator. Two acquisition modes:
/// <list type="bullet">
/// <item><description><b>Service launch</b> (default): the pooled, proxy-aware, stealth-injected browser.</description></item>
/// <item><description><b>CDP connect</b> (<see cref="ScrapingOptions.CdpEndpoint"/> set): attach over CDP to an
/// externally-launched <em>non-leaky</em> driver (nodriver/patchright/Camoufox-class) so the
/// <c>Runtime.enable</c> automation signal is never emitted.</description></item>
/// </list>
/// </summary>
public sealed class BrowserWebFetcher : IWebFetcher
{
    private static readonly string[] ChallengeMarkers =
    [
        "Just a moment...", "cf-browser-verification", "Checking your browser before accessing",
        "Attention Required! | Cloudflare", "/cdn-cgi/challenge-platform", "px-captcha",
    ];

    private readonly IBrowserService _browser;
    private readonly ScrapingOptions _options;
    private readonly IReviLogger<BrowserWebFetcher>? _logger;

    /// <summary>Creates the browser fetcher over the shared browser pool and scraping options.</summary>
    public BrowserWebFetcher(IBrowserService browser, ScrapingOptions options, IReviLogger<BrowserWebFetcher>? logger = null)
    {
        _browser = browser;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public WebFetchTier Tier => WebFetchTier.BrowserStealth;

    /// <inheritdoc/>
    public Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(_options.CdpEndpoint)
            ? FetchViaServiceAsync(request, cancellationToken)
            : FetchViaCdpAsync(request, cancellationToken);

    /// <summary>Fetches via the pooled <see cref="IBrowserService"/> (launches/reuses Chromium, applies stealth).</summary>
    private async Task<FetchResult> FetchViaServiceAsync(FetchRequest request, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await using IPage page = await _browser.AcquirePageAsync(new BrowserRequestOptions
            {
                ConsumerKey = "web-content",
                InitialUrl = request.Url,
                NavigationTimeoutMs = request.TimeoutMs,
                UserAgentOverride = request.UserAgent,
                OptimizeNetwork = false, // keep full content for extraction
            }, ct);

            string html = await page.GetContentAsync();
            sw.Stop();
            return Build(html, page.Url, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogWarning($"BrowserWebFetcher: service fetch failed for {request.Url}: {ex.Message}");
            return Error(request.Url, sw.ElapsedMilliseconds, ex);
        }
    }

    /// <summary>Fetches by connecting over CDP to an externally-launched (non-leaky) browser.</summary>
    private async Task<FetchResult> FetchViaCdpAsync(FetchRequest request, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        IBrowser? browser = null;
        IPage? page = null;
        try
        {
            browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = _options.CdpEndpoint });
            page = await browser.NewPageAsync();

            if (!string.IsNullOrWhiteSpace(request.UserAgent))
                await page.SetUserAgentAsync(request.UserAgent);

            await page.EvaluateExpressionOnNewDocumentAsync(StealthScripts.NavigatorEvasions);
            await page.GoToAsync(request.Url, new NavigationOptions
            {
                Timeout = request.TimeoutMs,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            });

            string html = await page.GetContentAsync();
            string finalUrl = page.Url;
            sw.Stop();
            return Build(html, finalUrl, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogWarning($"BrowserWebFetcher: CDP fetch failed for {request.Url}: {ex.Message}");
            return Error(request.Url, sw.ElapsedMilliseconds, ex);
        }
        finally
        {
            if (page is not null) { try { await page.CloseAsync(); } catch { /* best effort */ } }
            // Disconnect (do NOT close) the externally-owned browser.
            if (browser is not null) { try { browser.Disconnect(); } catch { /* best effort */ } }
        }
    }

    /// <summary>Builds a successful result, flagging likely challenge interstitials.</summary>
    private FetchResult Build(string? html, string finalUrl, long elapsedMs)
    {
        string body = html ?? string.Empty;
        bool blocked = LooksChallenged(body);
        return new FetchResult
        {
            Html = body,
            FinalUrl = finalUrl,
            StatusCode = 200,
            ContentType = "text/html",
            Tier = Tier,
            ElapsedMs = elapsedMs,
            Blocked = blocked,
            Note = blocked ? "browser-challenged" : null,
        };
    }

    /// <summary>Builds a failed (blocked) result carrying the error note.</summary>
    private FetchResult Error(string url, long elapsedMs, Exception ex) => new()
    {
        Html = string.Empty,
        FinalUrl = url,
        StatusCode = 0,
        Tier = Tier,
        ElapsedMs = elapsedMs,
        Blocked = true,
        Note = $"browser-error: {ex.Message}",
    };

    /// <summary>Heuristic: does the rendered HTML still look like a bot-challenge interstitial?</summary>
    private static bool LooksChallenged(string html)
    {
        if (string.IsNullOrEmpty(html)) return true;
        string head = html.Length > 4096 ? html[..4096] : html;
        foreach (string marker in ChallengeMarkers)
            if (head.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
