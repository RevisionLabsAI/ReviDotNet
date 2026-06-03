// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Thread-safe crawl frontier with per-domain round-robin scheduling, so one slow or link-heavy
/// domain cannot starve the others. Retries can be pushed to the <em>forefront</em> of their domain's
/// queue. (SQLite-backed persistence for resumable crawls is a documented extension.)
/// </summary>
public sealed class RequestQueue
{
    /// <summary>A queued URL with its crawl depth and priority.</summary>
    public readonly record struct CrawlItem(string Url, int Depth, int Priority = 0);

    private readonly Dictionary<string, LinkedList<CrawlItem>> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _hosts = [];
    private readonly object _lock = new();
    private int _roundRobin;
    private int _count;

    /// <summary>Number of queued items across all domains.</summary>
    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>Enqueues an item under its domain; <paramref name="forefront"/> jumps it ahead (used for retries).</summary>
    public void Enqueue(CrawlItem item, bool forefront = false)
    {
        string host = UrlCanonicalizer.SiteKey(item.Url);
        lock (_lock)
        {
            if (!_byHost.TryGetValue(host, out LinkedList<CrawlItem>? list))
            {
                list = new LinkedList<CrawlItem>();
                _byHost[host] = list;
                _hosts.Add(host);
            }

            if (forefront) list.AddFirst(item);
            else list.AddLast(item);
            _count++;
        }
    }

    /// <summary>Dequeues the next item using round-robin across domains; returns false if empty.</summary>
    public bool TryDequeue(out CrawlItem item)
    {
        item = default;
        lock (_lock)
        {
            if (_count == 0 || _hosts.Count == 0) return false;

            for (int i = 0; i < _hosts.Count; i++)
            {
                int idx = (_roundRobin + i) % _hosts.Count;
                LinkedList<CrawlItem> list = _byHost[_hosts[idx]];
                if (list.Count > 0)
                {
                    item = list.First!.Value;
                    list.RemoveFirst();
                    _count--;
                    _roundRobin = idx + 1;
                    return true;
                }
            }

            return false;
        }
    }
}
