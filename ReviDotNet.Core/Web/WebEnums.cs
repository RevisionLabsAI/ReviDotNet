// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Capability tier of a fetcher, used by the tiered escalator to pick the cheapest fetcher that can
/// clear a given target. Higher values are more capable (and more expensive).
/// </summary>
public enum WebFetchTier
{
    /// <summary>Plain <c>HttpClient</c> GET. Cheapest; cannot run JS or pass JS/TLS challenges.</summary>
    Http = 0,

    /// <summary>Real headless browser (JS rendering, authentic TLS/HTTP-2 fingerprint).</summary>
    Browser = 1,

    /// <summary>Real browser with the full stealth/anti-bot evasion set and session pinning.</summary>
    BrowserStealth = 2,
}

/// <summary>How a page should be rendered/acquired.</summary>
public enum RenderMode
{
    /// <summary>Let the fetcher decide; a tiered fetcher escalates HTTP → browser on block/empty signals.</summary>
    Auto = 0,

    /// <summary>Force the plain HTTP path; never launch a browser.</summary>
    HttpOnly = 1,

    /// <summary>Force a real browser even if the HTTP path might have worked.</summary>
    Browser = 2,
}

/// <summary>Output representation requested from a fetch/extract operation.</summary>
public enum WebOutputFormat
{
    /// <summary>Clean, metadata-tagged Markdown (the default LLM-ready form).</summary>
    Markdown = 0,

    /// <summary>The cleaned main-content HTML before Markdown conversion.</summary>
    Html = 1,

    /// <summary>Plain text of the main content.</summary>
    Text = 2,
}
