// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace Revi;

/// <summary>
/// Optional cache of fetched/extracted documents, keyed by canonical URL (+ output discriminator).
/// Off by default — register an implementation to enable it. A persistent (e.g. SQLite/Redis) variant
/// is a documented extension.
/// </summary>
public interface IWebContentCache
{
    /// <summary>Tries to retrieve a cached, non-expired document for the key.</summary>
    bool TryGet(string key, out WebDocument document);

    /// <summary>Stores a document under the key.</summary>
    void Set(string key, WebDocument document);
}

/// <summary>
/// In-memory <see cref="IWebContentCache"/> with a TTL and a bounded entry count (oldest-expiry
/// eviction on overflow). Thread-safe.
/// </summary>
public sealed class InMemoryWebContentCache : IWebContentCache
{
    private readonly record struct Entry(WebDocument Document, DateTime ExpiresUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    /// <summary>Creates the cache with a time-to-live and maximum entry count.</summary>
    public InMemoryWebContentCache(TimeSpan? ttl = null, int maxEntries = 1000)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(15);
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <inheritdoc/>
    public bool TryGet(string key, out WebDocument document)
    {
        document = null!;
        if (!_entries.TryGetValue(key, out Entry entry)) return false;
        if (entry.ExpiresUtc <= DateTime.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return false;
        }
        document = entry.Document;
        return true;
    }

    /// <inheritdoc/>
    public void Set(string key, WebDocument document)
    {
        _entries[key] = new Entry(document, DateTime.UtcNow.Add(_ttl));

        // Bound the cache: when over capacity, drop the soonest-to-expire entries.
        if (_entries.Count <= _maxEntries) return;
        foreach (KeyValuePair<string, Entry> stale in _entries.OrderBy(kv => kv.Value.ExpiresUtc).Take(_entries.Count - _maxEntries))
            _entries.TryRemove(stale.Key, out _);
    }
}
