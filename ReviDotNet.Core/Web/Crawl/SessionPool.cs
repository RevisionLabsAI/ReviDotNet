// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// A pool of scored <see cref="Session"/> identities, modelled on Crawlee's <c>SessionPool</c>. It
/// grows lazily up to a cap, hands out a <em>random</em> usable session (random, not round-robin, to
/// avoid a predictable rotation a defender can fingerprint), and retires sessions that get blocked or
/// age out. This is the design that most differentiates a serious scraper from a naive one.
/// </summary>
public sealed class SessionPool
{
    /// <summary>Maximum number of live sessions (Crawlee default: 1000).</summary>
    public const int DefaultMaxPoolSize = 1000;

    /// <summary>Error score at which a session is considered blocked (Crawlee default: 3).</summary>
    public const double MaxErrorScore = 3.0;

    /// <summary>Uses after which a session is retired (Crawlee default: 50).</summary>
    public const int MaxUsageCount = 50;

    /// <summary>Status codes that force immediate session retirement (Crawlee default).</summary>
    public static readonly IReadOnlySet<int> BlockedStatusCodes = new HashSet<int> { 401, 403, 429 };

    /// <summary>Age after which a session is retired (Crawlee default: 50 minutes).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(50);

    private readonly int _maxPoolSize;
    private readonly List<ScrapeSession> _sessions = [];
    private readonly object _lock = new();
    private int _counter;

    /// <summary>Creates a pool with the given size cap.</summary>
    public SessionPool(int maxPoolSize = DefaultMaxPoolSize) => _maxPoolSize = Math.Max(1, maxPoolSize);

    /// <summary>Current number of live (not-yet-pruned) sessions.</summary>
    public int Count
    {
        get { lock (_lock) return _sessions.Count; }
    }

    /// <summary>
    /// Returns a usable session, pruning dead ones first and lazily creating a fresh identity when the
    /// pool has room and few usable sessions remain. The pick among usable sessions is random.
    /// </summary>
    public ScrapeSession GetSession(DateTime nowUtc)
    {
        lock (_lock)
        {
            _sessions.RemoveAll(s => !s.IsUsable(nowUtc));

            List<ScrapeSession> usable = _sessions; // after prune, all remaining are usable
            bool roomToGrow = _sessions.Count < _maxPoolSize;
            if (usable.Count == 0 || roomToGrow)
            {
                ScrapeSession created = CreateSession(nowUtc);
                _sessions.Add(created);
                // Bias toward exercising the freshest identity when the pool is otherwise empty.
                if (usable.Count == 1) return created;
            }

            return _sessions[Random.Shared.Next(_sessions.Count)];
        }
    }

    /// <summary>
    /// Records the outcome of a request made with a session: a blocked status code retires it
    /// immediately; otherwise success/failure adjusts its score.
    /// </summary>
    public void ReportOutcome(ScrapeSession session, bool success, int? statusCode = null)
    {
        if (statusCode is int code && BlockedStatusCodes.Contains(code))
        {
            session.Retire();
            return;
        }

        if (success) session.MarkGood();
        else session.MarkBad();
    }

    /// <summary>Number of sessions currently blocked (diagnostics/testing).</summary>
    public int BlockedCount
    {
        get { lock (_lock) return _sessions.Count(s => s.IsBlocked); }
    }

    private ScrapeSession CreateSession(DateTime nowUtc)
        => new($"sess-{++_counter}", MaxErrorScore, MaxUsageCount, MaxAge, nowUtc);
}
