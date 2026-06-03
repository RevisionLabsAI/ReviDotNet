// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// High-level entry point that turns a URL — or a crawl request — into clean,
/// metadata-tagged, LLM-ready content. This is the surface other ReviDotNet
/// consumers (and the built-in web tools/agents) use.
/// </summary>
public interface IWebContentService
{
    /// <summary>Fetches a single URL and returns an LLM-ready document.</summary>
    /// <param name="url">Absolute URL to fetch.</param>
    /// <param name="options">Per-call overrides (render mode, output format, anti-bot tier).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebDocument> FetchAsync(
        string url,
        WebFetchOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Crawls from seed URLs, yielding documents as each page completes.</summary>
    IAsyncEnumerable<WebDocument> CrawlAsync(
        WebCrawlRequest request,
        CancellationToken cancellationToken = default);
}
