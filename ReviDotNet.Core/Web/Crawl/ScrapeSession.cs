// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net;

namespace Revi;

/// <summary>
/// One scored identity (cookie jar + optional headers/proxy) in a <see cref="SessionPool"/>, modelled
/// on Crawlee's <c>Session</c>. The asymmetric scoring (a success undoes only half of a failure) makes
/// the pool quick to retire an identity that starts getting blocked and slow to trust it again.
/// <para>
/// <b>Status: standalone primitive — NOT consumed by any production fetch path</b> (unit-test-only
/// today). See <see cref="SessionPool"/> for details; the live Core/Scraping fetchers do not use
/// per-request session identities or cookie jars.
/// </para>
/// </summary>
public sealed class ScrapeSession
{
    /// <summary>Amount an error subtracts... see <see cref="MarkGood"/> (Crawlee constant: 0.5).</summary>
    private const double GoodDelta = 0.5;

    /// <summary>Amount a failure adds to the error score (Crawlee constant: 1).</summary>
    private const double BadDelta = 1.0;

    private readonly double _maxErrorScore;
    private readonly int _maxUsageCount;
    private readonly TimeSpan _maxAge;

    /// <summary>Creates a session with the pool's retirement thresholds.</summary>
    public ScrapeSession(string id, double maxErrorScore, int maxUsageCount, TimeSpan maxAge, DateTime createdAtUtc)
    {
        Id = id;
        _maxErrorScore = maxErrorScore;
        _maxUsageCount = maxUsageCount;
        _maxAge = maxAge;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Stable identifier for diagnostics.</summary>
    public string Id { get; }

    /// <summary>Per-session cookie jar, so an identity keeps its trust cookies across requests.</summary>
    public CookieContainer Cookies { get; } = new();

    /// <summary>Optional pinned proxy key for this identity (set by the fetcher); null means no preference.</summary>
    public string? ProxyKey { get; set; }

    /// <summary>Optional pinned user agent / header identity for this session.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Current error score; rises on failures, falls on successes, capped at zero below.</summary>
    public double ErrorScore { get; private set; }

    /// <summary>How many times this session has been used.</summary>
    public int UsageCount { get; private set; }

    /// <summary>When the session was created (used for age-based retirement).</summary>
    public DateTime CreatedAtUtc { get; }

    /// <summary>Records a successful use: drops the error score by 0.5 (floored at 0) and counts a use.</summary>
    public void MarkGood()
    {
        ErrorScore = Math.Max(0, ErrorScore - GoodDelta);
        UsageCount++;
    }

    /// <summary>Records a failed use: raises the error score by 1 and counts a use.</summary>
    public void MarkBad()
    {
        ErrorScore += BadDelta;
        UsageCount++;
    }

    /// <summary>Immediately retires the session (e.g. on a hard block status), making it unusable.</summary>
    public void Retire() => ErrorScore = _maxErrorScore;

    /// <summary>Whether the session is blocked (error score at or above the max).</summary>
    public bool IsBlocked => ErrorScore >= _maxErrorScore;

    /// <summary>Whether the session can still be handed out: not blocked, under usage cap, under age cap.</summary>
    public bool IsUsable(DateTime nowUtc)
        => !IsBlocked && UsageCount < _maxUsageCount && (nowUtc - CreatedAtUtc) < _maxAge;
}
