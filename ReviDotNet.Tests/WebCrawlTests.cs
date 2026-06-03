// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Verifies the Phase 2 crawl infrastructure: URL canonicalization/dedup, Crawlee-style session
/// scoring, AutoThrottle delay math, retry decisions, robots.txt parsing/matching (fail-open), the
/// per-domain round-robin queue, and an end-to-end bounded crawl over a fake fetcher (no network).
/// </summary>
public class WebCrawlTests
{
    // ---- Canonicalization & dedup ----------------------------------------------------------------

    [Fact]
    public void Canonicalizer_LowercasesHost_StripsDefaultPort_SortsQuery_DropsFragment()
    {
        UrlCanonicalizer.Canonicalize("HTTP://Example.COM:80/Path?b=2&a=1#frag")
            .Should().Be("http://example.com/Path?a=1&b=2");
        UrlCanonicalizer.Canonicalize("https://x.test:443/")
            .Should().Be("https://x.test/");
        UrlCanonicalizer.SiteKey("https://www.x.test/a").Should().Be("x.test");
    }

    [Fact]
    public void DupeFilter_CollapsesCanonicallyEqualUrls()
    {
        var dupe = new InMemoryDupeFilter();
        dupe.TryAdd("https://x.test/a?b=2&a=1").Should().BeTrue();
        dupe.TryAdd("https://x.test/a?a=1&b=2").Should().BeFalse(); // reordered query → same canonical
        dupe.TryAdd("https://x.test/a").Should().BeTrue();          // no query → distinct
        dupe.Count.Should().Be(2);
    }

    // ---- Session scoring -------------------------------------------------------------------------

    [Fact]
    public void Session_ScoringIsAsymmetric_AndRetirementThresholdsHold()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var s = new ScrapeSession("s", SessionPool.MaxErrorScore, SessionPool.MaxUsageCount, SessionPool.MaxAge, now);
        s.MarkBad();
        s.ErrorScore.Should().Be(1.0);
        s.MarkGood();
        s.ErrorScore.Should().Be(0.5); // success undoes only half a failure

        s.MarkBad(); s.MarkBad(); s.MarkBad(); // push to/over the block threshold
        s.IsBlocked.Should().BeTrue();

        var usageCapped = new ScrapeSession("u", 3, 2, TimeSpan.FromMinutes(50), now);
        usageCapped.MarkGood();
        usageCapped.IsUsable(now).Should().BeTrue();
        usageCapped.MarkGood();
        usageCapped.IsUsable(now).Should().BeFalse(); // usage cap reached

        var aged = new ScrapeSession("a", 3, 50, TimeSpan.FromMinutes(50), now.AddMinutes(-60));
        aged.IsUsable(now).Should().BeFalse(); // older than max age
    }

    [Fact]
    public void SessionPool_RetiresOnBlockedStatus_AndHandsOutUsableSessions()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var pool = new SessionPool();

        ScrapeSession s = pool.GetSession(now);
        s.IsUsable(now).Should().BeTrue();

        pool.ReportOutcome(s, success: false, statusCode: 403); // hard block
        s.IsBlocked.Should().BeTrue();

        ScrapeSession next = pool.GetSession(now);
        next.IsUsable(now).Should().BeTrue();           // pool pruned the blocked one and grew a fresh one
        next.Should().NotBeSameAs(s);
    }

    // ---- AutoThrottle ----------------------------------------------------------------------------

    [Fact]
    public async Task DomainThrottle_AdaptsDelay_AndErrorsNeverDecreaseIt()
    {
        var throttle = new DomainThrottle(targetConcurrency: 1, startDelayMs: 100, minDelayMs: 1, maxDelayMs: 100_000);

        await throttle.AcquireAsync("h", CancellationToken.None);
        throttle.Release("h", latencyMs: 20, ok: true);   // target=20, new=(100+20)/2=60, max(20,60)=60
        throttle.CurrentDelayMs("h").Should().BeApproximately(60, 0.001);

        await throttle.AcquireAsync("h", CancellationToken.None);
        throttle.Release("h", latencyMs: 10, ok: false);  // would compute 35, but non-200 must not decrease → stays 60
        throttle.CurrentDelayMs("h").Should().Be(60);
    }

    // ---- Retry policy ----------------------------------------------------------------------------

    [Fact]
    public void RetryPolicy_ClassifiesStatusesAndExceptions_AndHonorsRetryAfter()
    {
        var p = RetryPolicy.Default;
        p.IsRetryableStatus(503).Should().BeTrue();
        p.IsRetryableStatus(429).Should().BeTrue();
        p.IsRetryableStatus(404).Should().BeFalse();
        p.IsRetryableStatus(403).Should().BeFalse();

        RetryPolicy.IsRetryableException(new HttpRequestException()).Should().BeTrue();
        RetryPolicy.IsRetryableException(new InvalidOperationException()).Should().BeFalse();

        p.GetDelay(1, TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromSeconds(2)); // Retry-After honored
        p.GetDelay(1, TimeSpan.FromHours(1)).Should().Be(p.MaxDelay);                // clamped
        TimeSpan backoff = p.GetDelay(2);
        backoff.Should().BeGreaterThan(TimeSpan.Zero);
        backoff.Should().BeLessThanOrEqualTo(p.MaxDelay);
    }

    // ---- robots.txt ------------------------------------------------------------------------------

    [Fact]
    public void Robots_LongestMatchWins_AllowBeatsDisallow_AndCrawlDelayParsed()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /private\nAllow: /private/ok\nCrawl-delay: 2");
        rules.IsAllowed("/public").Should().BeTrue();
        rules.IsAllowed("/private/x").Should().BeFalse();
        rules.IsAllowed("/private/ok").Should().BeTrue(); // longer Allow wins over shorter Disallow
        rules.CrawlDelay.Should().Be(TimeSpan.FromSeconds(2));

        // Specific UA group with a blanket disallow does not affect the "*" group.
        var star = RobotsRules.Parse("User-agent: badbot\nDisallow: /\n\nUser-agent: *\nDisallow:", "*");
        star.IsAllowed("/anything").Should().BeTrue();
    }

    [Fact]
    public async Task RobotsCache_FailsOpen_AndEnforcesDisallow()
    {
        var failOpen = new RobotsTxtCache("*", (_, _) => Task.FromResult<string?>(null));
        (await failOpen.IsAllowedAsync(new Uri("https://x.test/a"), CancellationToken.None)).Should().BeTrue();

        var enforcing = new RobotsTxtCache("*", (_, _) => Task.FromResult<string?>("User-agent: *\nDisallow: /a"));
        (await enforcing.IsAllowedAsync(new Uri("https://x.test/a/b"), CancellationToken.None)).Should().BeFalse();
        (await enforcing.IsAllowedAsync(new Uri("https://x.test/b"), CancellationToken.None)).Should().BeTrue();
    }

    // ---- Request queue ---------------------------------------------------------------------------

    [Fact]
    public void RequestQueue_RoundRobinsAcrossDomains_AndForefrontJumpsAhead()
    {
        var q = new RequestQueue();
        q.Enqueue(new RequestQueue.CrawlItem("https://a.test/1", 0));
        q.Enqueue(new RequestQueue.CrawlItem("https://a.test/2", 0));
        q.Enqueue(new RequestQueue.CrawlItem("https://b.test/1", 0));

        q.TryDequeue(out var i1).Should().BeTrue();
        q.TryDequeue(out var i2).Should().BeTrue();
        q.TryDequeue(out var i3).Should().BeTrue();

        i1.Url.Should().Contain("a.test");
        i2.Url.Should().Contain("b.test"); // round-robin moved to the other domain
        i3.Url.Should().Contain("a.test");
        q.Count.Should().Be(0);

        q.Enqueue(new RequestQueue.CrawlItem("https://c.test/1", 0));
        q.Enqueue(new RequestQueue.CrawlItem("https://c.test/2", 0), forefront: true);
        q.TryDequeue(out var f).Should().BeTrue();
        f.Url.Should().EndWith("/2"); // forefront retry jumped ahead of /1
    }

    // ---- End-to-end crawl ------------------------------------------------------------------------

    [Fact]
    public async Task Crawl_FollowsSameSiteLinks_Dedups_AndYieldsAllReachablePages()
    {
        var pages = new Dictionary<string, string>
        {
            ["https://site.test/"] = "<html><body><a href=\"/a\">a</a><a href=\"/b\">b</a></body></html>",
            ["https://site.test/a"] = "<html><body><a href=\"/b\">b</a><a href=\"/c\">c</a><a href=\"https://other.test/x\">ext</a></body></html>",
            ["https://site.test/b"] = "<html><body><p>leaf b content</p></body></html>",
            ["https://site.test/c"] = "<html><body><p>leaf c content</p></body></html>",
        };

        var service = NewService(new SiteFetcher(pages));
        var request = new WebCrawlRequest
        {
            SeedUrls = ["https://site.test/"],
            MaxPages = 10,
            MaxDepth = 2,
            SameSiteOnly = true,
            MaxConcurrency = 2,
            FetchOptions = new WebFetchOptions { RespectRobots = false },
        };

        List<string> urls = [];
        await foreach (WebDocument doc in service.CrawlAsync(request, CancellationToken.None))
            urls.Add(doc.Url);

        urls.Should().HaveCount(4);
        urls.Should().Contain(["https://site.test/", "https://site.test/a", "https://site.test/b", "https://site.test/c"]);
        urls.Should().NotContain("https://other.test/x"); // same-site filter excluded the external link
    }

    [Fact]
    public async Task Crawl_RespectsMaxPagesBudget()
    {
        var pages = new Dictionary<string, string>
        {
            ["https://site.test/"] = "<html><body><a href=\"/a\">a</a><a href=\"/b\">b</a><a href=\"/c\">c</a></body></html>",
            ["https://site.test/a"] = "<html><body><p>a</p></body></html>",
            ["https://site.test/b"] = "<html><body><p>b</p></body></html>",
            ["https://site.test/c"] = "<html><body><p>c</p></body></html>",
        };

        var service = NewService(new SiteFetcher(pages));
        var request = new WebCrawlRequest
        {
            SeedUrls = ["https://site.test/"],
            MaxPages = 2,
            MaxDepth = 3,
            MaxConcurrency = 2,
            FetchOptions = new WebFetchOptions { RespectRobots = false },
        };

        int count = 0;
        await foreach (WebDocument _ in service.CrawlAsync(request, CancellationToken.None))
            count++;

        count.Should().BeLessThanOrEqualTo(2);
        count.Should().BeGreaterThanOrEqualTo(1);
    }

    private static WebContentService NewService(IWebFetcher fetcher)
        => new(fetcher,
            new ReadabilityContentExtractor(),
            new ReverseMarkdownConverter(),
            new StructuredDataMetadataExtractor(),
            new HeadingTokenChunker());

    /// <summary>An <see cref="IWebFetcher"/> serving a fixed in-memory site graph; unknown URLs 404.</summary>
    private sealed class SiteFetcher(Dictionary<string, string> pages) : IWebFetcher
    {
        public WebFetchTier Tier => WebFetchTier.Http;

        public Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
        {
            bool has = pages.TryGetValue(request.Url, out string? html);
            return Task.FromResult(new FetchResult
            {
                Html = html ?? string.Empty,
                FinalUrl = request.Url,
                StatusCode = has ? 200 : 404,
                ContentType = "text/html",
                Tier = Tier,
                ElapsedMs = 1,
                Blocked = !has,
            });
        }
    }
}
