// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Acquires the raw HTML/final DOM for a URL. Implementations differ by anti-bot power; the seam lets
/// the rest of the pipeline stay identical regardless of which fetcher (HTTP vs. browser) served a page.
/// </summary>
public interface IWebFetcher
{
    /// <summary>Capability tier this fetcher provides (used by the tiered escalator).</summary>
    WebFetchTier Tier { get; }

    /// <summary>Performs the fetch, applying session/headers/proxy from the request.</summary>
    Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Inputs to a single fetch.</summary>
public sealed record FetchRequest
{
    /// <summary>Absolute URL to fetch.</summary>
    public required string Url { get; init; }

    /// <summary>How the page should be rendered/acquired.</summary>
    public RenderMode RenderMode { get; init; } = RenderMode.Auto;

    /// <summary>Ceiling on fetch tier the escalator may use for this request.</summary>
    public WebFetchTier MaxTier { get; init; } = WebFetchTier.Browser;

    /// <summary>Optional user-agent override.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Optional extra request headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Per-request timeout in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 30_000;

    /// <summary>Whether the caller asked to obey robots.txt for this fetch.</summary>
    public bool RespectRobots { get; init; } = true;
}

/// <summary>Output of a single fetch: the raw document plus transport diagnostics.</summary>
public sealed record FetchResult
{
    /// <summary>The raw HTML body (or text) returned by the target.</summary>
    public required string Html { get; init; }

    /// <summary>Final URL after redirects.</summary>
    public required string FinalUrl { get; init; }

    /// <summary>HTTP status code (0 if not applicable).</summary>
    public int StatusCode { get; init; }

    /// <summary>Response content type, if known.</summary>
    public string? ContentType { get; init; }

    /// <summary>The tier that served this result.</summary>
    public WebFetchTier Tier { get; init; }

    /// <summary>Wall-clock fetch time in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Whether the fetcher judged the response to be a block/challenge.</summary>
    public bool Blocked { get; init; }

    /// <summary>Free-form diagnostic note.</summary>
    public string? Note { get; init; }
}
