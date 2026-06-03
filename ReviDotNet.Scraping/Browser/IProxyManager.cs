// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Centralized proxy registry + parser. Always exposes a "default" (no-proxy) entry so callers can
/// assume at least one key exists.
/// </summary>
public interface IProxyManager
{
    /// <summary>Initializes the registry from an optional proxy list file plus environment variables (idempotent).</summary>
    Task InitializeAsync(string? proxyFilePath, CancellationToken cancellationToken = default);

    /// <summary>Returns all available proxy keys, including "default" when no proxies are configured.</summary>
    IReadOnlyList<string> GetProxyKeys();

    /// <summary>Gets a proxy by key, or the "default" (no-proxy) entry for null/"default".</summary>
    ProxyEntry? GetProxy(string? key);
}

/// <summary>A single proxy endpoint with optional credentials.</summary>
public class ProxyEntry
{
    /// <summary>Unique key (e.g. host:port or a label).</summary>
    public string Key { get; init; } = "default";

    /// <summary>Proxy scheme (http or socks5).</summary>
    public string Scheme { get; init; } = "http";

    /// <summary>Proxy host; empty for the no-proxy "default" entry.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Proxy port.</summary>
    public int Port { get; init; }

    /// <summary>Optional proxy username.</summary>
    public string? Username { get; init; }

    /// <summary>Optional proxy password.</summary>
    public string? Password { get; init; }

    /// <summary>A stable endpoint identifier used as the rate-limit key; "default" when there is no host.</summary>
    public string Endpoint => string.IsNullOrWhiteSpace(Host) ? "default" : $"{Host}:{Port}";

    /// <summary>Renders a <c>--proxy-server=</c> Chromium launch argument, or empty for the no-proxy entry.</summary>
    public string ToProxyServerArg()
        => string.IsNullOrWhiteSpace(Host) ? string.Empty : $"--proxy-server={Scheme}://{Host}:{Port}";

    /// <summary>Renders a full proxy URI with credentials inline (for HTTP clients that accept it), or empty.</summary>
    public string ToUriWithCredentials()
        => string.IsNullOrWhiteSpace(Host)
            ? string.Empty
            : string.IsNullOrEmpty(Username) ? $"{Scheme}://{Host}:{Port}" : $"{Scheme}://{Username}:{Password}@{Host}:{Port}";
}
