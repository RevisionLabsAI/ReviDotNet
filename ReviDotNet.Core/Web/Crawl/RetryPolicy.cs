// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.IO;
using System.Net.Sockets;

namespace Revi;

/// <summary>
/// Retry decision logic, modelled on Scrapy's retry middleware with two improvements: exponential
/// backoff with jitter, and honoring the <c>Retry-After</c> header. Retries transient HTTP statuses
/// and transport exceptions; never retries client errors like 400/401/403/404 (those are hard
/// failures — for 401/403 the right move is to escalate to a browser tier, not to retry).
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Transient HTTP status codes worth retrying (Scrapy's set plus 522/524).</summary>
    private static readonly IReadOnlySet<int> RetryableStatuses =
        new HashSet<int> { 408, 429, 500, 502, 503, 504, 522, 524 };

    /// <summary>Maximum retry attempts after the initial try.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Base backoff delay; doubled each attempt before jitter.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on any single backoff delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A shared default policy.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Whether the HTTP status is one worth retrying.</summary>
    public bool IsRetryableStatus(int statusCode) => RetryableStatuses.Contains(statusCode);

    /// <summary>Whether the exception is a transient transport fault worth retrying (excludes user cancellation).</summary>
    public static bool IsRetryableException(Exception ex) => ex switch
    {
        HttpRequestException => true,
        SocketException => true,
        IOException => true,
        TimeoutException => true,
        TaskCanceledException tce => tce.InnerException is TimeoutException, // request timeout, not user cancel
        _ => false,
    };

    /// <summary>
    /// Computes the delay before the given retry <paramref name="attempt"/> (1-based). Honors an
    /// explicit <paramref name="retryAfter"/> when present; otherwise exponential backoff with
    /// 0.5×–1.5× jitter, clamped to <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan GetDelay(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is TimeSpan ra && ra > TimeSpan.Zero)
            return ra <= MaxDelay ? ra : MaxDelay;

        double exp = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        double jitter = 0.5 + Random.Shared.NextDouble(); // 0.5 .. 1.5
        double ms = Math.Min(MaxDelay.TotalMilliseconds, exp * jitter);
        return TimeSpan.FromMilliseconds(ms);
    }
}
