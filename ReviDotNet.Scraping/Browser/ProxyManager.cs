// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Revi;

/// <summary>
/// Centralized proxy registry + parser used by <see cref="BrowserService"/>.
/// - Reads an optional proxy list file once (idempotent <see cref="InitializeAsync"/>).
/// - Keeps a thread-safe map of proxies by key.
/// - Always exposes a "default" (no proxy) entry so callers can assume at least one key exists.
///
/// SmartProxy support:
/// You can add SmartProxy lines directly to the proxies file; the parser supports common formats.
/// Examples (quotes optional when no spaces):
///   - http://us.smartproxy.net:3120 smart-fbnkt3w1cg9f_area-US aoXLhvh83kaa8gxi
///   - us.smartproxy.net:3120 "smart-fbnkt3w1cg9f_area-US" "aoXLhvh83kaa8gxi"
///   - http://smart-fbnkt3w1cg9f_area-US:aoXLhvh83kaa8gxi@us.smartproxy.net:3120
/// Scheme defaults to http when omitted.
/// </summary>
public class ProxyManager : IProxyManager
{
    private readonly ConcurrentDictionary<string, ProxyEntry> _proxies = new();
    private readonly object _initLock = new();
    private bool _initialized;

    /// <summary>
    /// Initialize from an optional proxies file. Idempotent and thread-safe.
    /// Lines starting with '#' are comments. Blank lines are skipped.
    /// Supported formats per line (SmartProxy compatible):
    ///   1) scheme://host:port
    ///   2) scheme://user:pass@host:port
    ///   3) host:port
    ///   4) host:port user pass
    ///   5) scheme://host:port user pass
    ///   6) label scheme://host:port [user pass]
    ///   7) label host:port [user pass]
    /// Where label is any identifier without spaces; quotes are supported for user/pass.
    /// </summary>
    public Task InitializeAsync(string? proxyFilePath, CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.CompletedTask;
        lock (_initLock)
        {
            if (_initialized) return Task.CompletedTask;
            if (!string.IsNullOrWhiteSpace(proxyFilePath) && File.Exists(proxyFilePath))
            {
                foreach (string? raw in File.ReadAllLines(proxyFilePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? line = raw?.Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;        // skip empty
                    if (line.StartsWith("#")) continue;                   // skip comments

                    ProxyEntry? entry = ParseProxyLine(line);
                    if (entry != null)
                    {
                        _proxies[entry.Key] = entry;                      // last one wins by key
                    }
                }
            }

            // Environment-based configuration provider (optional)
            try
            {
                // Simple single-proxy URL, e.g.:
                //   BROWSER_PROXY_URL=http://user:pass@host:port
                //   BROWSER_PROXY_URL=host:port
                // Optional: BROWSER_PROXY_LABEL=my-proxy
                string? envUrl = Environment.GetEnvironmentVariable("BROWSER_PROXY_URL");
                if (!string.IsNullOrWhiteSpace(envUrl))
                {
                    string? label = Environment.GetEnvironmentVariable("BROWSER_PROXY_LABEL");
                    string line = string.IsNullOrWhiteSpace(label) ? envUrl!.Trim() : $"{label.Trim()} {envUrl!.Trim()}";
                    ProxyEntry? entry = ParseProxyLine(line);
                    if (entry != null)
                    {
                        _proxies[entry.Key] = entry;
                    }
                }

                // SmartProxy-style vars
                //   BROWSER_SMARTPROXY_HOST=us.smartproxy.io
                //   BROWSER_SMARTPROXY_PORT=3128
                //   BROWSER_SMARTPROXY_USERNAME=xxx
                //   BROWSER_SMARTPROXY_PASSWORD=yyy
                // Optional: BROWSER_SMARTPROXY_LABEL=smartproxy
                // Optional: BROWSER_SMARTPROXY_SCHEME=http (default)
                string? spHost = Environment.GetEnvironmentVariable("BROWSER_SMARTPROXY_HOST");
                string? spPort = Environment.GetEnvironmentVariable("BROWSER_SMARTPROXY_PORT");
                if (!string.IsNullOrWhiteSpace(spHost) && !string.IsNullOrWhiteSpace(spPort))
                {
                    string? spUser = Environment.GetEnvironmentVariable("BROWSER_SMARTPROXY_USERNAME");
                    string? spPass = Environment.GetEnvironmentVariable("BROWSER_SMARTPROXY_PASSWORD");
                    string? spLabel = Environment.GetEnvironmentVariable("BROWSER_SMARTPROXY_LABEL");
                    string? spScheme = Environment.GetEnvironmentVariable("BROWSER_SMARTPROXY_SCHEME");
                    if (string.IsNullOrWhiteSpace(spScheme)) spScheme = "http";

                    string line;
                    if (!string.IsNullOrWhiteSpace(spUser) && !string.IsNullOrWhiteSpace(spPass))
                    {
                        line = string.IsNullOrWhiteSpace(spLabel)
                            ? $"{spScheme}://{spHost}:{spPort} {spUser} {spPass}"
                            : $"{spLabel} {spScheme}://{spHost}:{spPort} {spUser} {spPass}";
                    }
                    else
                    {
                        line = string.IsNullOrWhiteSpace(spLabel)
                            ? $"{spScheme}://{spHost}:{spPort}"
                            : $"{spLabel} {spScheme}://{spHost}:{spPort}";
                    }

                    ProxyEntry? entry = ParseProxyLine(line);
                    if (entry != null)
                    {
                        _proxies[entry.Key] = entry;
                    }
                }
            }
            catch
            {
                // ignore env parsing issues; keep going
            }

            // Always ensure default (no-proxy) key exists
            _proxies.TryAdd("default", new ProxyEntry { Key = "default" });
            _initialized = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Returns all known proxy keys. "default" (no proxy) is listed first for predictability.
    /// </summary>
    public IReadOnlyList<string> GetProxyKeys()
        => _proxies.Keys.OrderBy(k => k == "default" ? 0 : 1).ToList();

    /// <summary>
    /// Gets a proxy by key. Returns the "default" (no proxy) entry when key is null/empty or equals "default".
    /// </summary>
    public ProxyEntry? GetProxy(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == "default") return _proxies["default"];
        return _proxies.TryGetValue(key, out ProxyEntry? p) ? p : null;
    }

    /// <summary>
    /// Parses a single line into a ProxyEntry. Accepts multiple common formats to ease configuration.
    /// This includes SmartProxy credentials either inline (user:pass@host:port) or as extra tokens.
    /// </summary>
    private static ProxyEntry? ParseProxyLine(string line)
    {
        // Supported formats:
        // 1) scheme://host:port
        // 2) scheme://user:pass@host:port
        // 3) host:port
        // 4) host:port user pass
        // 5) scheme://host:port user pass
        // 6) label scheme://host:port [user pass]
        // 7) label host:port [user pass]
        try
        {
            string[] parts = SplitRespectingQuotes(line);
            if (parts.Length == 0) return null;

            // local helper to check if a token looks like an endpoint (uri or host:port)
            static bool IsEndpointToken(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                if (token.Contains("://")) return true;
                int idx = token.LastIndexOf(':');
                if (idx <= 0 || idx == token.Length - 1) return false;
                string portPart = token[(idx + 1)..];
                return int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            }

            int idxStart = 0;
            string? label = null;
            // If first token is not an endpoint but second is, treat first as label
            if (parts.Length >= 2 && !IsEndpointToken(parts[0]) && IsEndpointToken(parts[1]))
            {
                label = Unquote(parts[0]);
                idxStart = 1;
            }

            string first = parts[idxStart];
            string? user = null; string? pass = null; string scheme = "http";
            string host; int port;

            if (first.Contains("://"))
            {
                // ex: http://user:pass@host:port OR http://host:port
                Uri uri = new Uri(first);
                scheme = uri.Scheme;
                host = uri.Host;
                port = uri.Port;
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    string[] ui = uri.UserInfo.Split(':');
                    if (ui.Length > 0) user = Uri.UnescapeDataString(ui[0]);
                    if (ui.Length > 1) pass = Uri.UnescapeDataString(ui[1]);
                }
                // If credentials not included in URI, allow them as following tokens
                if ((parts.Length - idxStart) >= 3 && string.IsNullOrEmpty(user))
                {
                    user = Unquote(parts[idxStart + 1]);
                    pass = Unquote(parts[idxStart + 2]);
                }
            }
            else
            {
                // ex: host:port [user pass]
                string[] hp = first.Split(':');
                if (hp.Length != 2) return null;
                host = hp[0];
                port = int.Parse(hp[1], CultureInfo.InvariantCulture);
                if ((parts.Length - idxStart) >= 3)
                {
                    user = Unquote(parts[idxStart + 1]);
                    pass = Unquote(parts[idxStart + 2]);
                }
            }

            string key = !string.IsNullOrWhiteSpace(label)
                ? label!
                : (string.IsNullOrWhiteSpace(host) ? "default" : $"{host}:{port}");
            return new ProxyEntry
            {
                Key = key,
                Scheme = scheme,
                Host = host,
                Port = port,
                Username = user,
                Password = pass
            };
        }
        catch
        {
            return null;
        }
    }

    private static string[] SplitRespectingQuotes(string input)
    {
        MatchCollection matches = Regex.Matches(input, @"""[^""]*""|\S+");
        return matches.Select(m => m.Value).ToArray();
    }

    /// <summary>
    /// Removes surrounding quotes and trims whitespace.
    /// </summary>
    private static string Unquote(string s)
        => s.Trim().Trim('"');
}
