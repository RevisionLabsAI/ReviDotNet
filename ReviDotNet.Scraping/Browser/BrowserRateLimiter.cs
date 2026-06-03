// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace Revi;

/// <summary>
/// Simple sliding-window rate limiter keyed by (target IP/endpoint, consumer). Each bucket tracks
/// request timestamps over a one-minute window and awaits when the cap is reached.
/// </summary>
public class BrowserRateLimiter
{
    /// <summary>A per-key bucket of recent request timestamps guarded by its own lock.</summary>
    private class Bucket
    {
        public readonly object Lock = new();
        public readonly Queue<DateTime> Timestamps = new();
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    private static string MakeKey(string key, string consumer) => $"{key}::{consumer}";

    /// <summary>
    /// Blocks (asynchronously) until a request to <paramref name="key"/> for <paramref name="consumer"/> is
    /// permitted under <paramref name="maxPerMinute"/>. A non-positive cap disables limiting.
    /// </summary>
    public async Task EnsureAllowedAsync(string key, string consumer, int maxPerMinute, CancellationToken ct)
    {
        if (maxPerMinute <= 0) return; // disabled
        string k = MakeKey(key, consumer);
        Bucket bucket = _buckets.GetOrAdd(k, _ => new Bucket());

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DateTime now = DateTime.UtcNow;
            DateTime windowStart = now.AddMinutes(-1);
            TimeSpan? delayNeeded = null;
            lock (bucket.Lock)
            {
                // purge old timestamps
                while (bucket.Timestamps.Count > 0 && bucket.Timestamps.Peek() < windowStart)
                    _ = bucket.Timestamps.Dequeue();

                if (bucket.Timestamps.Count < maxPerMinute)
                {
                    bucket.Timestamps.Enqueue(now);
                    return; // allowed
                }
                else
                {
                    DateTime oldest = bucket.Timestamps.Peek();
                    TimeSpan wait = oldest.AddMinutes(1) - now;
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                    delayNeeded = wait;
                }
            }
            if (delayNeeded.HasValue)
            {
                await Task.Delay(delayNeeded.Value, ct);
            }
        }
    }
}
