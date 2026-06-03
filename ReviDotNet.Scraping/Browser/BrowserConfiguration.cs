// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;

namespace Revi;

/// <summary>
/// Strongly-typed options for the browser-driven scraping stack
/// (<see cref="BrowserService"/>, <see cref="ProxyManager"/>, <see cref="BrowserRateLimiter"/>).
///
/// This is a plain options record so ReviDotNet stays stateless: a host binds it from
/// configuration (see <see cref="From"/>) or constructs it directly, then passes it in via
/// <c>AddReviScraping</c>. The library never reaches into <see cref="IConfiguration"/> at runtime.
/// </summary>
public class BrowserConfiguration
{
    /// <summary>Optional path to a proxy list file loaded once at startup. Null disables file loading.</summary>
    public string? ProxyListPath { get; set; }

    /// <summary>Default per-IP requests-per-minute cap applied when a request does not override it.</summary>
    public int DefaultMaxRequestsPerMinutePerIp { get; set; } = 60;

    /// <summary>Optional delay (ms) relative to process start before the first browser launch, to smooth resource spikes.</summary>
    public int InitialStartupDelayMs { get; set; } = 0;

    /// <summary>BCP-47 locale emulated by stealth (Accept-Language and navigator.language).</summary>
    public string DefaultLocale { get; set; } = "en-US";

    /// <summary>IANA timezone id emulated by stealth (e.g. <c>America/Los_Angeles</c>).</summary>
    public string DefaultTimezone { get; set; } = "America/Los_Angeles";

    /// <summary>Whether to inject stealth tweaks on every acquired page.</summary>
    public bool StealthEnabled { get; set; } = true;

    /// <summary>
    /// Optional path to a real, installed Chrome/Chromium executable. Driving a current real Chrome
    /// (rather than the bundled headless Chromium) keeps the TLS/HTTP-2 fingerprint authentic with no
    /// manual upkeep. Null uses PuppeteerSharp's downloaded browser.
    /// </summary>
    public string? ChromeExecutablePath { get; set; }

    /// <summary>
    /// Builds a <see cref="BrowserConfiguration"/> from the <c>Browser:*</c> section of an
    /// <see cref="IConfiguration"/>. Centralizes the key mapping so hosts can keep their settings in
    /// <c>appsettings.json</c> while the service itself takes only the populated record.
    /// </summary>
    /// <param name="configuration">The host configuration to read <c>Browser:*</c> keys from.</param>
    /// <returns>A populated configuration with sensible defaults for any missing keys.</returns>
    public static BrowserConfiguration From(IConfiguration configuration)
    {
        BrowserConfiguration cfg = new BrowserConfiguration();
        cfg.ProxyListPath = configuration["Browser:ProxyListPath"];

        if (int.TryParse(configuration["Browser:DefaultMaxRequestsPerMinutePerIp"], out int rpm) && rpm > 0)
            cfg.DefaultMaxRequestsPerMinutePerIp = rpm;

        if (int.TryParse(configuration["Browser:InitialStartupDelayMs"], out int delay) && delay >= 0)
            cfg.InitialStartupDelayMs = delay;

        cfg.DefaultLocale = configuration["Browser:DefaultLocale"] ?? cfg.DefaultLocale;
        cfg.DefaultTimezone = configuration["Browser:DefaultTimezone"] ?? cfg.DefaultTimezone;
        if (bool.TryParse(configuration["Browser:Stealth:Enabled"], out bool stealth))
            cfg.StealthEnabled = stealth;
        cfg.ChromeExecutablePath = configuration["Browser:ChromeExecutablePath"];

        return cfg;
    }
}
