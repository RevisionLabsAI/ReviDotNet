// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using PuppeteerSharp;

namespace Revi;

/// <summary>
/// High-level browser/page acquisition over a pooled, proxy-aware Chromium fleet.
/// Pages are owned by the caller, who must dispose them after use.
/// </summary>
public interface IBrowserService : IAsyncDisposable, IDisposable
{
    /// <summary>Ensures Chromium is available and proxies are initialized (idempotent).</summary>
    Task InitAsync(CancellationToken cancellationToken = default);

    /// <summary>Acquires a ready-to-use page. The page is owned by the caller who must dispose it after use.</summary>
    Task<IPage> AcquirePageAsync(BrowserRequestOptions options, CancellationToken cancellationToken = default);

    /// <summary>Acquires multiple pages for batch operations; the service may distribute them across proxies.</summary>
    Task<IReadOnlyList<IPage>> AcquirePagesAsync(int count, BrowserRequestOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Per-call options controlling proxy selection, rate limiting, stealth UA, and initial navigation.</summary>
public record BrowserRequestOptions
{
    /// <summary>A logical consumer id used to separate rate-limit buckets across services.</summary>
    public string ConsumerKey { get; init; } = "default";

    /// <summary>Max requests-per-minute per target IP for this consumer; null uses the configured default.</summary>
    public int? MaxRequestsPerMinutePerIp { get; init; } = null;

    /// <summary>Force usage of a specific proxy label or endpoint (optional); null lets the service rotate.</summary>
    public string? PreferredProxyKey { get; init; } = null;

    /// <summary>Optional user-agent override; null uses a generated modern Chrome UA.</summary>
    public string? UserAgentOverride { get; init; } = null;

    /// <summary>Navigate immediately to this URL after the page is created (optional).</summary>
    public string? InitialUrl { get; init; } = null;

    /// <summary>Navigation timeout in milliseconds for the initial navigation; null uses 30s.</summary>
    public int? NavigationTimeoutMs { get; init; } = null;

    /// <summary>Whether to block images/styles/fonts/media to speed up and cheapen page loads.</summary>
    public bool OptimizeNetwork { get; init; } = false;
}
