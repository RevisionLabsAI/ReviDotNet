// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// The default <see cref="IWebFetcher"/> once <c>ReviDotNet.Scraping</c> is registered. Tries the cheap
/// <see cref="HttpWebFetcher"/> first and escalates to the <see cref="BrowserWebFetcher"/> only when the
/// HTTP response shows a block/challenge, an error/empty body, or a suspiciously short body — so the
/// expensive browser runs only when it's actually needed. <see cref="RenderMode"/> and
/// <see cref="FetchRequest.MaxTier"/> let callers force or forbid the browser tier.
/// </summary>
public sealed class TieredWebFetcher : IWebFetcher
{
    private readonly IWebFetcher _http;
    private readonly IWebFetcher _browser;
    private readonly ScrapingOptions _options;
    private readonly IReviLogger<TieredWebFetcher>? _logger;

    /// <summary>
    /// Creates the escalator over the HTTP and browser fetchers. Typed as <see cref="IWebFetcher"/> so
    /// the decision logic is unit-testable with fakes; DI supplies the concrete
    /// <see cref="HttpWebFetcher"/> and <see cref="BrowserWebFetcher"/>.
    /// </summary>
    public TieredWebFetcher(IWebFetcher http, IWebFetcher browser, ScrapingOptions options, IReviLogger<TieredWebFetcher>? logger = null)
    {
        _http = http;
        _browser = browser;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public WebFetchTier Tier => WebFetchTier.BrowserStealth;

    /// <inheritdoc/>
    public async Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
    {
        // Caller forces the browser.
        if (request.RenderMode == RenderMode.Browser)
            return await _browser.FetchAsync(request, cancellationToken);

        FetchResult http = await _http.FetchAsync(request, cancellationToken);

        // Caller forbids escalation, or the request's tier ceiling doesn't permit a browser.
        if (request.RenderMode == RenderMode.HttpOnly) return http;
        if (request.MaxTier < WebFetchTier.Browser) return http;
        if (!_options.AutoEscalate) return http;
        if (!ShouldEscalate(http)) return http;

        _logger?.LogDebug($"TieredWebFetcher: escalating {request.Url} to browser " +
                          $"({http.Note ?? $"status {http.StatusCode}, {http.Html?.Length ?? 0} chars"}).");

        FetchResult browser = await _browser.FetchAsync(request, cancellationToken);
        return PreferBetter(http, browser);
    }

    /// <summary>Whether the cheap HTTP result is poor enough to justify the browser.</summary>
    private bool ShouldEscalate(FetchResult http)
        => http.Blocked
           || http.StatusCode == 0
           || http.StatusCode >= 400
           || string.IsNullOrWhiteSpace(http.Html)
           || http.Html!.Length < _options.ShortBodyThreshold;

    /// <summary>Keeps the HTTP result if the browser fared no better; otherwise prefers the browser.</summary>
    private static FetchResult PreferBetter(FetchResult http, FetchResult browser)
    {
        if (browser.Blocked && !http.Blocked) return http;
        if (string.IsNullOrWhiteSpace(browser.Html) && !string.IsNullOrWhiteSpace(http.Html)) return http;
        return browser;
    }
}
