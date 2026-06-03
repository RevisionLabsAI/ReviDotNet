// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Describes a bounded crawl: seed URLs plus limits and a same-site/URL filter. Consumed by
/// <see cref="IWebContentService.CrawlAsync"/>.
/// </summary>
public sealed record WebCrawlRequest
{
    /// <summary>One or more absolute URLs to start from.</summary>
    public required IReadOnlyList<string> SeedUrls { get; init; }

    /// <summary>Maximum number of pages to fetch before stopping.</summary>
    public int MaxPages { get; init; } = 50;

    /// <summary>Maximum link depth from the seeds (0 = seeds only).</summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>When true, only follow links on the same registrable site as the seed.</summary>
    public bool SameSiteOnly { get; init; } = true;

    /// <summary>Per-page fetch options applied to every crawled URL.</summary>
    public WebFetchOptions FetchOptions { get; init; } = new();

    /// <summary>Maximum concurrent in-flight page fetches.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Optional predicate to include/exclude a discovered URL before it is enqueued.</summary>
    public Func<string, bool>? UrlFilter { get; init; }
}
