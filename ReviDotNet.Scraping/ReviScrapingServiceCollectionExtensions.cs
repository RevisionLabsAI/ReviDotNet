// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Revi;

/// <summary>
/// Extension methods for registering the ReviDotNet.Scraping browser stack with
/// <see cref="IServiceCollection"/>. Opt-in companion to <c>AddReviDotNet</c>: call it when a consumer
/// needs JS rendering or anti-bot fetching via a real browser. It registers the browser pool and
/// replaces the default <see cref="IWebFetcher"/> with a <see cref="TieredWebFetcher"/> that escalates
/// HTTP → browser on demand.
/// </summary>
public static class ReviScrapingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the browser stack and tiered fetcher, binding <see cref="BrowserConfiguration"/> from
    /// <c>Browser:*</c> and <see cref="ScrapingOptions"/> from <c>Scraping:*</c>.
    /// </summary>
    public static IServiceCollection AddReviScraping(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddReviScraping(BrowserConfiguration.From(configuration), ScrapingOptions.From(configuration));

    /// <summary>Registers the browser stack and tiered fetcher using explicit options.</summary>
    public static IServiceCollection AddReviScraping(
        this IServiceCollection services,
        BrowserConfiguration browserConfig,
        ScrapingOptions? scrapingOptions = null)
    {
        // TryAdd so a consumer can substitute any piece before calling this.
        services.TryAddSingleton(browserConfig);
        services.TryAddSingleton(scrapingOptions ?? new ScrapingOptions());
        services.TryAddSingleton<BrowserRateLimiter>();
        services.TryAddSingleton<IProxyManager, ProxyManager>();
        services.TryAddSingleton<IBrowserService, BrowserService>();

        // Fetchers: HTTP (cheap) + Browser (anti-bot) behind a tiered escalator. Register the escalator
        // as IWebFetcher so it supersedes Core's default HttpWebFetcher registration (last wins).
        services.TryAddSingleton<HttpWebFetcher>();
        services.TryAddSingleton<BrowserWebFetcher>();
        services.AddSingleton<IWebFetcher>(sp => new TieredWebFetcher(
            sp.GetRequiredService<HttpWebFetcher>(),
            sp.GetRequiredService<BrowserWebFetcher>(),
            sp.GetRequiredService<ScrapingOptions>(),
            sp.GetService<IReviLogger<TieredWebFetcher>>()));

        return services;
    }

    /// <summary>Registers the browser stack, building <see cref="BrowserConfiguration"/> via a callback.</summary>
    public static IServiceCollection AddReviScraping(
        this IServiceCollection services,
        Action<BrowserConfiguration> configure)
    {
        BrowserConfiguration cfg = new();
        configure(cfg);
        return services.AddReviScraping(cfg);
    }
}
