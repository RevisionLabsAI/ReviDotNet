// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace Revi;

/// <summary>
/// Per-domain politeness governor implementing Scrapy's AutoThrottle: adapt the inter-request delay to
/// observed server latency toward a target concurrency, with multiplicative jitter and a guard that
/// non-200 responses can never <em>decrease</em> the delay (error pages are small/fast and would
/// otherwise speed you up exactly when the server is unhappy). A per-host gate serializes requests so
/// the adaptive delay is actually honored, and a robots <c>Crawl-delay</c> raises the floor.
/// </summary>
public sealed class DomainThrottle
{
    private readonly double _targetConcurrency;
    private readonly double _minDelayMs;
    private readonly double _maxDelayMs;
    private readonly double _startDelayMs;
    private readonly ConcurrentDictionary<string, DomainSlot> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a throttle with AutoThrottle parameters (delays in milliseconds).</summary>
    public DomainThrottle(
        double targetConcurrency = 1.0,
        double startDelayMs = 1000,
        double minDelayMs = 250,
        double maxDelayMs = 30_000)
    {
        _targetConcurrency = Math.Max(0.1, targetConcurrency);
        _startDelayMs = startDelayMs;
        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
    }

    /// <summary>Per-host state: a serializing gate, the current adaptive delay, and the next allowed time.</summary>
    private sealed class DomainSlot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public double DelayMs;
        public double CrawlDelayFloorMs;
        public DateTime NextAllowedUtc = DateTime.MinValue;
    }

    private DomainSlot GetSlot(string host)
        => _slots.GetOrAdd(host, _ => new DomainSlot { DelayMs = _startDelayMs });

    /// <summary>Raises the per-host delay floor from a robots <c>Crawl-delay</c> (idempotent, takes the max).</summary>
    public void SetCrawlDelay(string host, TimeSpan? crawlDelay)
    {
        if (crawlDelay is not TimeSpan d || d <= TimeSpan.Zero) return;
        DomainSlot slot = GetSlot(host);
        slot.CrawlDelayFloorMs = Math.Max(slot.CrawlDelayFloorMs, d.TotalMilliseconds);
    }

    /// <summary>
    /// Acquires the per-host turn: waits for the gate, then sleeps until the (jittered) next-allowed
    /// time. Pair every call with <see cref="Release"/> in a finally block.
    /// </summary>
    public async Task AcquireAsync(string host, CancellationToken cancellationToken)
    {
        DomainSlot slot = GetSlot(host);
        await slot.Gate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan wait = slot.NextAllowedUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
        }
        catch
        {
            slot.Gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Updates the adaptive delay from the just-observed latency (AutoThrottle), sets the next-allowed
    /// time using a 0.5×–1.5× jitter, and releases the per-host gate.
    /// </summary>
    public void Release(string host, double latencyMs, bool ok)
    {
        DomainSlot slot = GetSlot(host);
        try
        {
            double target = latencyMs / _targetConcurrency;
            double newDelay = (slot.DelayMs + target) / 2.0;
            newDelay = Math.Max(target, newDelay);
            newDelay = Math.Clamp(newDelay, _minDelayMs, _maxDelayMs);

            // Non-200 responses must not decrease the delay (avoid positive feedback on errors).
            if (!ok && newDelay < slot.DelayMs)
                newDelay = slot.DelayMs;

            slot.DelayMs = newDelay;

            double effective = Math.Max(slot.DelayMs, slot.CrawlDelayFloorMs);
            double jitter = 0.5 + Random.Shared.NextDouble(); // 0.5 .. 1.5
            slot.NextAllowedUtc = DateTime.UtcNow.AddMilliseconds(effective * jitter);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Current adaptive delay for a host, in milliseconds (diagnostics/testing).</summary>
    public double CurrentDelayMs(string host) => GetSlot(host).DelayMs;
}
