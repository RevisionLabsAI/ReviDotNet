// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Verifies the Phase 3 anti-bot pieces that don't require launching a real browser: coherent header
/// generation, the tiered HTTP→browser escalation decision (with fake fetchers), and that the stealth
/// evasion script contains its key patches.
/// </summary>
public class WebAntiBotTests
{
    // ---- Header generation -----------------------------------------------------------------------

    [Fact]
    public void HeaderGenerator_ProducesCoherentChromeProfile()
    {
        BrowserHeaderProfile p = new HeaderGenerator(seed: 12345).Generate();

        p.UserAgent.Should().Contain($"Chrome/{p.ChromeMajor}.");
        string secChUa = p.Headers.First(h => h.Key == "sec-ch-ua").Value;
        secChUa.Should().Contain($"\"Google Chrome\";v=\"{p.ChromeMajor}\"");

        string platform = p.Headers.First(h => h.Key == "sec-ch-ua-platform").Value;
        if (p.UserAgent.Contains("Windows NT")) platform.Should().Be("\"Windows\"");
        else if (p.UserAgent.Contains("Mac OS X")) platform.Should().Be("\"macOS\"");
        else platform.Should().Be("\"Linux\"");

        p.Headers.Should().Contain(h => h.Key == "sec-ch-ua-mobile" && h.Value == "?0");
        p.Headers.Should().Contain(h => h.Key == "User-Agent");
        p.Headers.Should().Contain(h => h.Key == "Accept-Language");
    }

    [Fact]
    public void HeaderGenerator_IsDeterministic_WithSeed()
    {
        new HeaderGenerator(42).Generate().UserAgent
            .Should().Be(new HeaderGenerator(42).Generate().UserAgent);
    }

    // ---- Stealth script --------------------------------------------------------------------------

    [Fact]
    public void StealthScripts_ContainKeyEvasions()
    {
        string s = StealthScripts.NavigatorEvasions;
        s.Should().Contain("webdriver");
        s.Should().Contain("chrome");
        s.Should().Contain("getParameter"); // WebGL spoof
        s.Should().Contain("plugins");
        s.Should().Contain("[native code]"); // toString masking
    }

    // ---- Tiered escalation -----------------------------------------------------------------------

    [Fact]
    public async Task Tiered_GoodHttp_DoesNotEscalate()
    {
        var http = new StubFetcher(Ok(new string('x', 1500)), WebFetchTier.Http);
        var browser = new StubFetcher(Ok("<html>browser</html>"), WebFetchTier.BrowserStealth);
        var tiered = new TieredWebFetcher(http, browser, new ScrapingOptions());

        FetchResult result = await tiered.FetchAsync(new FetchRequest { Url = "https://x.test/" }, CancellationToken.None);

        browser.Calls.Should().Be(0);
        result.Tier.Should().Be(WebFetchTier.Http);
    }

    [Fact]
    public async Task Tiered_BlockedHttp_EscalatesToBrowser()
    {
        var http = new StubFetcher(Blocked(), WebFetchTier.Http);
        var browser = new StubFetcher(Ok("<html>rendered by browser</html>", WebFetchTier.BrowserStealth), WebFetchTier.BrowserStealth);
        var tiered = new TieredWebFetcher(http, browser, new ScrapingOptions());

        FetchResult result = await tiered.FetchAsync(new FetchRequest { Url = "https://x.test/" }, CancellationToken.None);

        browser.Calls.Should().Be(1);
        result.Tier.Should().Be(WebFetchTier.BrowserStealth);
    }

    [Fact]
    public async Task Tiered_ShortBody_Escalates()
    {
        var http = new StubFetcher(Ok("<html>tiny</html>"), WebFetchTier.Http);
        var browser = new StubFetcher(Ok(new string('y', 2000), WebFetchTier.BrowserStealth), WebFetchTier.BrowserStealth);
        var tiered = new TieredWebFetcher(http, browser, new ScrapingOptions { ShortBodyThreshold = 1000 });

        FetchResult result = await tiered.FetchAsync(new FetchRequest { Url = "https://x.test/" }, CancellationToken.None);

        browser.Calls.Should().Be(1);
        result.Tier.Should().Be(WebFetchTier.BrowserStealth);
    }

    [Fact]
    public async Task Tiered_HttpOnly_NeverEscalates()
    {
        var http = new StubFetcher(Blocked(), WebFetchTier.Http);
        var browser = new StubFetcher(Ok("browser"), WebFetchTier.BrowserStealth);
        var tiered = new TieredWebFetcher(http, browser, new ScrapingOptions());

        FetchResult result = await tiered.FetchAsync(
            new FetchRequest { Url = "https://x.test/", RenderMode = RenderMode.HttpOnly }, CancellationToken.None);

        browser.Calls.Should().Be(0);
        result.Blocked.Should().BeTrue();
    }

    [Fact]
    public async Task Tiered_MaxTierHttp_ForbidsEscalation()
    {
        var http = new StubFetcher(Blocked(), WebFetchTier.Http);
        var browser = new StubFetcher(Ok("browser"), WebFetchTier.BrowserStealth);
        var tiered = new TieredWebFetcher(http, browser, new ScrapingOptions());

        FetchResult result = await tiered.FetchAsync(
            new FetchRequest { Url = "https://x.test/", MaxTier = WebFetchTier.Http }, CancellationToken.None);

        browser.Calls.Should().Be(0);
        result.Blocked.Should().BeTrue();
    }

    [Fact]
    public async Task Tiered_RenderModeBrowser_ForcesBrowser()
    {
        var http = new StubFetcher(Ok(new string('x', 2000)), WebFetchTier.Http);
        var browser = new StubFetcher(Ok("forced browser"), WebFetchTier.BrowserStealth);
        var tiered = new TieredWebFetcher(http, browser, new ScrapingOptions());

        FetchResult result = await tiered.FetchAsync(
            new FetchRequest { Url = "https://x.test/", RenderMode = RenderMode.Browser }, CancellationToken.None);

        http.Calls.Should().Be(0);
        browser.Calls.Should().Be(1);
    }

    // ---- DI wiring -------------------------------------------------------------------------------

    [Fact]
    public void AddReviScraping_OverridesIWebFetcher_WithTieredFetcher_AndResolvesGraph()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebFetcher, HttpWebFetcher>(); // mimic Core's default registration
        services.AddReviScraping(new BrowserConfiguration());

        using ServiceProvider sp = services.BuildServiceProvider();

        // Tiered escalator supersedes the plain HTTP fetcher (last registration wins).
        sp.GetRequiredService<IWebFetcher>().Should().BeOfType<TieredWebFetcher>();
        // The full browser graph resolves with optional loggers defaulting to null (no AddReviDotNet).
        sp.GetRequiredService<IBrowserService>().Should().BeOfType<BrowserService>();
        sp.GetRequiredService<BrowserWebFetcher>().Should().NotBeNull();
    }

    private static FetchResult Ok(string html, WebFetchTier tier = WebFetchTier.Http) => new()
    {
        Html = html,
        FinalUrl = "https://x.test/",
        StatusCode = 200,
        Tier = tier,
        ElapsedMs = 1,
    };

    private static FetchResult Blocked() => new()
    {
        Html = "",
        FinalUrl = "https://x.test/",
        StatusCode = 403,
        Tier = WebFetchTier.Http,
        ElapsedMs = 1,
        Blocked = true,
        Note = "blocked",
    };

    /// <summary>An <see cref="IWebFetcher"/> that returns a canned result and counts invocations.</summary>
    private sealed class StubFetcher(FetchResult result, WebFetchTier tier) : IWebFetcher
    {
        public int Calls { get; private set; }
        public WebFetchTier Tier => tier;

        public Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
