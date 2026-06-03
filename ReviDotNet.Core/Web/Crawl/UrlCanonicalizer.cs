// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;

namespace Revi;

/// <summary>
/// Canonicalizes URLs for dedup, mirroring Scrapy's <c>w3lib.url.canonicalize_url</c>: lowercase the
/// scheme and host, drop the default port, sort query parameters, and drop the fragment. The path is
/// left case-sensitive (paths can be case-significant). The result is a stable key for the dupe filter.
/// </summary>
public static class UrlCanonicalizer
{
    /// <summary>
    /// Returns the canonical form of an absolute URL, or the trimmed input unchanged if it cannot be
    /// parsed as an absolute URI.
    /// </summary>
    public static string Canonicalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)) return url.Trim();

        string scheme = uri.Scheme.ToLowerInvariant();
        string host = uri.Host.ToLowerInvariant();

        StringBuilder sb = new();
        sb.Append(scheme).Append("://").Append(host);

        if (!uri.IsDefaultPort)
            sb.Append(':').Append(uri.Port);

        // Path: collapse an empty path to "/".
        string path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        sb.Append(path);

        string sortedQuery = SortQuery(uri.Query);
        if (sortedQuery.Length > 0)
            sb.Append('?').Append(sortedQuery);

        // Fragment intentionally dropped.
        return sb.ToString();
    }

    /// <summary>Sorts query parameters by key then value, preserving repeated keys, dropping the leading '?'.</summary>
    private static string SortQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        string trimmed = query.StartsWith('?') ? query[1..] : query;
        if (trimmed.Length == 0) return string.Empty;

        List<(string Key, string Value, bool HadEquals)> pairs = [];
        foreach (string part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) pairs.Add((part, string.Empty, false));
            else pairs.Add((part[..eq], part[(eq + 1)..], true));
        }

        return string.Join('&', pairs
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.HadEquals ? $"{p.Key}={p.Value}" : p.Key));
    }

    /// <summary>
    /// Returns the registrable-ish host used for "same-site" comparisons: the host with a leading
    /// <c>www.</c> stripped. (A full public-suffix list is a documented extension.)
    /// </summary>
    public static string SiteKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return string.Empty;
        string host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }
}
