// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace Revi;

/// <summary>
/// Parsed robots.txt rules for a single user-agent group: an ordered allow/deny list plus an optional
/// <c>Crawl-delay</c>. Matching follows the longest-match rule (most specific path wins, Allow wins
/// ties), which is the de-facto standard.
/// </summary>
public sealed class RobotsRules
{
    private readonly List<(string Pattern, bool Allow)> _rules;

    /// <summary>The <c>Crawl-delay</c> declared for the matched group, if any.</summary>
    public TimeSpan? CrawlDelay { get; }

    private RobotsRules(List<(string, bool)> rules, TimeSpan? crawlDelay)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
    }

    /// <summary>Rules that permit everything (used as the fail-open default).</summary>
    public static RobotsRules AllowAll { get; } = new([], null);

    /// <summary>Whether the given path (path + query) is allowed for the parsed group.</summary>
    public bool IsAllowed(string path)
    {
        if (string.IsNullOrEmpty(path)) path = "/";

        int longestDisallow = -1;
        int longestAllow = -1;
        foreach ((string pattern, bool allow) in _rules)
        {
            if (!PathMatches(pattern, path)) continue;
            if (allow) longestAllow = Math.Max(longestAllow, pattern.Length);
            else longestDisallow = Math.Max(longestDisallow, pattern.Length);
        }

        if (longestDisallow < 0) return true;       // nothing disallows it
        return longestAllow >= longestDisallow;     // Allow wins ties / more specific
    }

    /// <summary>
    /// Parses robots.txt content, returning the rules for the most specific group matching
    /// <paramref name="userAgentToken"/> (falling back to the <c>*</c> group).
    /// </summary>
    public static RobotsRules Parse(string content, string userAgentToken = "*")
    {
        List<Group> groups = [];
        Group? current = null;
        bool lastWasAgent = false;

        foreach (string rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string field = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                if (!lastWasAgent || current is null)
                {
                    current = new Group();
                    groups.Add(current);
                }
                current.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else
            {
                if (current is null) continue; // directive before any user-agent line
                current.Directives.Add((field, value));
                lastWasAgent = false;
            }
        }

        string token = userAgentToken.ToLowerInvariant();
        Group? chosen = groups.FirstOrDefault(g => g.Agents.Contains(token))
                        ?? groups.FirstOrDefault(g => g.Agents.Contains("*"));
        if (chosen is null) return AllowAll;

        List<(string, bool)> rules = [];
        TimeSpan? crawlDelay = null;
        foreach ((string f, string v) in chosen.Directives)
        {
            switch (f)
            {
                case "disallow":
                    if (!string.IsNullOrEmpty(v)) rules.Add((v, false));
                    break;
                case "allow":
                    if (!string.IsNullOrEmpty(v)) rules.Add((v, true));
                    break;
                case "crawl-delay":
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs) && secs > 0)
                        crawlDelay = TimeSpan.FromSeconds(secs);
                    break;
            }
        }
        return new RobotsRules(rules, crawlDelay);
    }

    /// <summary>Matches a robots path pattern (supporting <c>*</c> wildcards and a trailing <c>$</c> anchor) against a path.</summary>
    private static bool PathMatches(string pattern, string path)
    {
        if (pattern.Length == 0) return false;
        bool anchored = pattern.EndsWith('$');
        string p = anchored ? pattern[..^1] : pattern;

        string[] parts = p.Split('*');
        if (!path.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        int pos = parts[0].Length;

        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            int next = path.IndexOf(parts[i], pos, StringComparison.Ordinal);
            if (next < 0) return false;
            pos = next + parts[i].Length;
        }

        if (anchored) return pos == path.Length;
        return true;
    }

    /// <summary>A single robots.txt user-agent group (one or more agents + its directives).</summary>
    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<(string Field, string Value)> Directives { get; } = [];
    }
}

/// <summary>
/// Per-authority cache of parsed robots.txt rules. Lazily fetches once per host and <b>fails open</b>
/// (treats fetch/parse errors as allow-all), matching Scrapy's posture. The fetcher is injectable for
/// testing. Honors <c>Crawl-delay</c> for feeding the per-domain throttle.
/// </summary>
public sealed class RobotsTxtCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsRules>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Uri, CancellationToken, Task<string?>> _fetch;
    private readonly string _userAgentToken;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Creates the cache for the given UA token, optionally with a custom robots.txt fetcher (for tests).</summary>
    public RobotsTxtCache(string userAgentToken = "*", Func<Uri, CancellationToken, Task<string?>>? fetcher = null)
    {
        _userAgentToken = userAgentToken;
        _fetch = fetcher ?? DefaultFetch;
    }

    /// <summary>Gets the parsed rules for the URL's authority, fetching/parsing once and caching.</summary>
    public Task<RobotsRules> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        string authority = url.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return _cache.GetOrAdd(authority, _ => new Lazy<Task<RobotsRules>>(() => LoadAsync(url, cancellationToken))).Value;
    }

    /// <summary>Whether the URL may be crawled per its host's robots.txt (allow-all on any error).</summary>
    public async Task<bool> IsAllowedAsync(Uri url, CancellationToken cancellationToken)
        => (await GetAsync(url, cancellationToken)).IsAllowed(url.PathAndQuery);

    /// <summary>The host's declared <c>Crawl-delay</c>, if any.</summary>
    public async Task<TimeSpan?> GetCrawlDelayAsync(Uri url, CancellationToken cancellationToken)
        => (await GetAsync(url, cancellationToken)).CrawlDelay;

    private async Task<RobotsRules> LoadAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            Uri authority = new(url.GetLeftPart(UriPartial.Authority));
            Uri robotsUri = new(authority, "/robots.txt");
            string? content = await _fetch(robotsUri, cancellationToken);
            return string.IsNullOrWhiteSpace(content) ? RobotsRules.AllowAll : RobotsRules.Parse(content, _userAgentToken);
        }
        catch
        {
            return RobotsRules.AllowAll; // fail-open
        }
    }

    private static async Task<string?> DefaultFetch(Uri robotsUri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage resp = await Http.GetAsync(robotsUri, cancellationToken);
        if (!resp.IsSuccessStatusCode) return null; // 4xx/5xx → fail-open
        return await resp.Content.ReadAsStringAsync(cancellationToken);
    }

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new() { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
}
