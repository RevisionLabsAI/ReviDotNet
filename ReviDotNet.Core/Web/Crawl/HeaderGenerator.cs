// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Generates a <em>coherent</em>, correctly-ordered browser header set (the technique behind Apify's
/// <c>header-generator</c> / <c>browserforge</c>). Inconsistency is itself a detection signal: the
/// User-Agent must agree with <c>Sec-CH-UA</c>, <c>Sec-CH-UA-Platform</c>, and <c>Sec-CH-UA-Mobile</c>,
/// and the headers must arrive in the order a real Chrome sends them. A single generated profile is
/// meant to be reused for the life of an identity/session (not regenerated per request).
/// </summary>
public sealed class HeaderGenerator
{
    /// <summary>A platform's UA token and matching <c>Sec-CH-UA-Platform</c> value.</summary>
    private static readonly (string UaPlatform, string SecChPlatform)[] Platforms =
    [
        ("Windows NT 10.0; Win64; x64", "\"Windows\""),
        ("Macintosh; Intel Mac OS X 10_15_7", "\"macOS\""),
        ("X11; Linux x86_64", "\"Linux\""),
    ];

    /// <summary>Recent Chrome major versions to rotate across.</summary>
    private static readonly int[] ChromeMajors = [126, 127, 128, 129, 130];

    private readonly Random _rng;

    /// <summary>Creates a generator; pass a seed for deterministic output (tests).</summary>
    public HeaderGenerator(int? seed = null) => _rng = seed is int s ? new Random(s) : new Random();

    /// <summary>Generates one coherent header profile (UA + correctly-ordered header list).</summary>
    /// <param name="acceptLanguage">Accept-Language value; defaults to <c>en-US,en;q=0.9</c>.</param>
    public BrowserHeaderProfile Generate(string? acceptLanguage = null)
    {
        (string uaPlatform, string secChPlatform) = Platforms[_rng.Next(Platforms.Length)];
        int major = ChromeMajors[_rng.Next(ChromeMajors.Length)];
        string lang = string.IsNullOrWhiteSpace(acceptLanguage) ? "en-US,en;q=0.9" : acceptLanguage!;

        string userAgent =
            $"Mozilla/5.0 ({uaPlatform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";

        // GREASE brand list, mirroring Chrome's Sec-CH-UA shape and version agreement with the UA.
        string secChUa = $"\"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\", \"Not?A_Brand\";v=\"24\"";

        // Ordered exactly as a navigating Chrome emits them.
        List<KeyValuePair<string, string>> headers =
        [
            new("sec-ch-ua", secChUa),
            new("sec-ch-ua-mobile", "?0"),
            new("sec-ch-ua-platform", secChPlatform),
            new("Upgrade-Insecure-Requests", "1"),
            new("User-Agent", userAgent),
            new("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7"),
            new("Sec-Fetch-Site", "none"),
            new("Sec-Fetch-Mode", "navigate"),
            new("Sec-Fetch-User", "?1"),
            new("Sec-Fetch-Dest", "document"),
            new("Accept-Encoding", "gzip, deflate, br, zstd"),
            new("Accept-Language", lang),
        ];

        return new BrowserHeaderProfile(userAgent, secChPlatform, major, headers);
    }
}

/// <summary>A coherent set of browser request headers plus the salient values that must agree.</summary>
public sealed record BrowserHeaderProfile(
    string UserAgent,
    string SecChUaPlatform,
    int ChromeMajor,
    IReadOnlyList<KeyValuePair<string, string>> Headers);
