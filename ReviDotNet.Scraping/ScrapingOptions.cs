// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;

namespace Revi;

/// <summary>
/// Options for the anti-bot / browser fetching tier. Bind from the <c>Scraping:*</c> section or
/// construct directly and pass to <c>AddReviScraping</c>.
/// </summary>
public sealed class ScrapingOptions
{
    /// <summary>
    /// Optional CDP WebSocket endpoint (e.g. <c>ws://127.0.0.1:9222/devtools/browser/...</c>) of an
    /// externally-launched browser. When set, <see cref="BrowserWebFetcher"/> connects over CDP instead
    /// of launching its own Chromium — this is how you attach a <em>non-leaky driver</em>
    /// (nodriver/patchright/Camoufox-class) that does not emit the <c>Runtime.enable</c> automation
    /// signal. See the scraping report for how to launch one.
    /// </summary>
    public string? CdpEndpoint { get; set; }

    /// <summary>
    /// When true, the tiered fetcher escalates HTTP → browser automatically on block/empty/short-body
    /// signals. When false, the browser is used only when a request explicitly forces
    /// <see cref="RenderMode.Browser"/>.
    /// </summary>
    public bool AutoEscalate { get; set; } = true;

    /// <summary>Raw HTML length (characters) below which an HTTP response is treated as too thin and escalated.</summary>
    public int ShortBodyThreshold { get; set; } = 1000;

    /// <summary>Builds options from the <c>Scraping:*</c> configuration section.</summary>
    public static ScrapingOptions From(IConfiguration configuration)
    {
        ScrapingOptions o = new()
        {
            CdpEndpoint = configuration["Scraping:CdpEndpoint"],
        };
        if (bool.TryParse(configuration["Scraping:AutoEscalate"], out bool auto)) o.AutoEscalate = auto;
        if (int.TryParse(configuration["Scraping:ShortBodyThreshold"], out int t) && t >= 0) o.ShortBodyThreshold = t;
        return o;
    }
}
