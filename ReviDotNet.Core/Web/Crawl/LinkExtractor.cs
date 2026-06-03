// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using AngleSharp.Html.Parser;

namespace Revi;

/// <summary>
/// Extracts outbound <c>&lt;a href&gt;</c> links from a page for crawl frontier expansion. Resolves
/// relative URLs against the page base (honoring <c>&lt;base href&gt;</c>), keeps only http/https,
/// drops fragments, and de-duplicates within the page.
/// </summary>
public static class LinkExtractor
{
    /// <summary>Returns the distinct absolute http(s) links found on the page.</summary>
    public static IReadOnlyList<string> ExtractLinks(string html, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        AngleSharp.Html.Dom.IHtmlDocument doc;
        try
        {
            doc = new HtmlParser().ParseDocument(html);
        }
        catch
        {
            return [];
        }

        Uri effectiveBase = baseUrl;
        string? baseHref = doc.QuerySelector("base[href]")?.GetAttribute("href");
        if (!string.IsNullOrWhiteSpace(baseHref) && Uri.TryCreate(baseUrl, baseHref, out Uri? b))
            effectiveBase = b;

        List<string> links = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (AngleSharp.Dom.IElement anchor in doc.QuerySelectorAll("a[href]"))
        {
            string? raw = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.StartsWith('#') ||
                raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(effectiveBase, raw, out Uri? abs)) continue;
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) continue;

            // Drop the fragment for crawl purposes.
            string noFragment = abs.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);

            if (seen.Add(noFragment))
                links.Add(noFragment);
        }

        return links;
    }
}
