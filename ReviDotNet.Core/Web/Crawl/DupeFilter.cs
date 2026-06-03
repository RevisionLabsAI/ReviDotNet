// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Deduplicates URLs during a crawl. Implementations canonicalize before comparing so that
/// trivially-different URLs (query order, default port, fragment) collapse to one key.
/// </summary>
public interface IDupeFilter
{
    /// <summary>Records the URL as seen; returns true if it was newly added, false if already seen.</summary>
    bool TryAdd(string url);

    /// <summary>Whether the URL has already been seen.</summary>
    bool Contains(string url);

    /// <summary>Number of distinct URLs seen.</summary>
    int Count { get; }
}

/// <summary>
/// In-memory <see cref="IDupeFilter"/> backed by a canonical-URL <see cref="HashSet{T}"/>. A
/// SQLite/Bloom-backed variant for very large or resumable crawls is a documented extension.
/// </summary>
public sealed class InMemoryDupeFilter : IDupeFilter
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <inheritdoc/>
    public bool TryAdd(string url)
    {
        string key = UrlCanonicalizer.Canonicalize(url);
        lock (_lock) return _seen.Add(key);
    }

    /// <inheritdoc/>
    public bool Contains(string url)
    {
        string key = UrlCanonicalizer.Canonicalize(url);
        lock (_lock) return _seen.Contains(key);
    }

    /// <inheritdoc/>
    public int Count
    {
        get { lock (_lock) return _seen.Count; }
    }
}
