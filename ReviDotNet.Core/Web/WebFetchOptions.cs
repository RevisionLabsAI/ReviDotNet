// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Per-call overrides for <see cref="IWebContentService.FetchAsync"/>. Defaults favor the polite,
/// cheap path; raise <see cref="MaxTier"/> and set <see cref="RespectRobots"/> = false for aggressive
/// retrieval.
/// </summary>
public sealed record WebFetchOptions
{
    /// <summary>How the page should be rendered/acquired.</summary>
    public RenderMode RenderMode { get; init; } = RenderMode.Auto;

    /// <summary>
    /// Which representation <see cref="WebDocument.Content"/> returns. All three representations
    /// (<see cref="WebDocument.Markdown"/>, <see cref="WebDocument.Html"/>, <see cref="WebDocument.Text"/>)
    /// are always populated; this selects the default one. Defaults to <see cref="WebOutputFormat.Markdown"/>.
    /// </summary>
    public WebOutputFormat OutputFormat { get; init; } = WebOutputFormat.Markdown;

    /// <summary>
    /// Ceiling on fetch tier the escalator may use. Defaults to <see cref="WebFetchTier.Browser"/> so that
    /// registering <c>ReviDotNet.Scraping</c> enables HTTP→browser escalation automatically (harmless in
    /// Core-only setups, which have no browser fetcher). Set to <see cref="WebFetchTier.Http"/> to forbid
    /// the browser, or <see cref="WebFetchTier.BrowserStealth"/> to permit the full anti-bot tier.
    /// </summary>
    public WebFetchTier MaxTier { get; init; } = WebFetchTier.Browser;

    /// <summary>Whether to additionally split the Markdown into <see cref="WebChunk"/>s.</summary>
    public bool Chunk { get; init; } = false;

    /// <summary>Chunking parameters, used only when <see cref="Chunk"/> is true.</summary>
    public ChunkOptions ChunkOptions { get; init; } = new();

    /// <summary>Hard cap on raw body length (characters) accepted from a fetcher, to bound memory.</summary>
    public int MaxContentLength { get; init; } = 5_000_000;

    /// <summary>Per-request fetch timeout in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 30_000;

    /// <summary>Optional user-agent override for the fetch.</summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Whether to obey robots.txt. <c>true</c> (polite) by default; callers with a clear basis (e.g.
    /// first-party targets) may set this to <c>false</c> to maximize retrieval success.
    /// <para>
    /// <b>Scope:</b> this is honored only by <see cref="IWebContentService.CrawlAsync"/> (the crawler checks
    /// robots.txt and Crawl-delay before each page). It is a <b>no-op for single-URL
    /// <see cref="IWebContentService.FetchAsync"/></b> — an explicitly requested fetch is not robots-gated.
    /// </para>
    /// </summary>
    public bool RespectRobots { get; init; } = true;

    /// <summary>Optional extra request headers merged into the fetch.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>Parameters controlling heading- and token-aware chunking.</summary>
public sealed record ChunkOptions
{
    /// <summary>Target maximum tokens per chunk.</summary>
    public int MaxTokens { get; init; } = 400;

    /// <summary>Token overlap between adjacent chunks split from the same section (≈10–20%).</summary>
    public int OverlapTokens { get; init; } = 60;

    /// <summary>Chunks smaller than this (in tokens) are merged forward where possible.</summary>
    public int MinChunkTokens { get; init; } = 48;

    /// <summary>Whether to prepend the heading breadcrumb to each chunk's text.</summary>
    public bool PrependHeadingTrail { get; init; } = true;
}
