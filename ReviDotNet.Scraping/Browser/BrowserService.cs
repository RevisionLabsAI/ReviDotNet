// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using PuppeteerSharp;

namespace Revi;

/// <summary>
/// Raised when an initial navigation fails, carrying the proxy attribution so callers can
/// distinguish proxy-level faults from target-level faults.
/// </summary>
public class ProxyNavigationException : Exception
{
    /// <summary>The proxy key (label or endpoint) in use when navigation failed.</summary>
    public string ProxyKey { get; }

    /// <summary>The proxy endpoint (host:port or "default") in use when navigation failed.</summary>
    public string ProxyEndpoint { get; }

    /// <summary>The URL that failed to load.</summary>
    public string Url { get; }

    /// <summary>Creates a navigation exception with proxy/target attribution.</summary>
    public ProxyNavigationException(string message, Exception inner, string proxyKey, string proxyEndpoint, string url)
        : base(message, inner)
    {
        ProxyKey = proxyKey;
        ProxyEndpoint = proxyEndpoint;
        Url = url;
    }
}

/// <summary>
/// Provides high-level browser/page acquisition over PuppeteerSharp with:
/// - Optional per-proxy Chromium instances and rotation
/// - Per-IP rate limiting via <see cref="BrowserRateLimiter"/>
/// - Basic stealth tweaks and network optimization
/// - Optional startup delay to avoid immediate resource spikes
///
/// Functionality is kept minimal and predictable; this service is thread-safe for Init and
/// caches one browser per proxy endpoint.
/// </summary>
public class BrowserService : IBrowserService
{
    private readonly BrowserConfiguration _browserConfig;
    private readonly IProxyManager _proxyManager;
    private readonly BrowserRateLimiter _rateLimiter;
    private readonly Func<PuppeteerSharp.LaunchOptions, Task<IBrowser>> _launcher;
    private readonly Func<Task> _ensureChromium;

    private bool _initialized;
    private readonly object _initLock = new();

    // Optional initial startup delay (ms) relative to application start
    private readonly int _initialStartupDelayMs = 0;
    private readonly DateTime _appStartUtc = DateTime.UtcNow;

    /// <summary>
    /// Internal holder for a browser instance bound to a specific proxy.
    /// </summary>
    private class BrowserHolder
    {
        public IBrowser Browser = default!;
        public ProxyEntry Proxy = default!;
    }

    private readonly ConcurrentDictionary<string, BrowserHolder> _browsers = new();
    private int _roundRobinIndex = 0;

    /// <summary>
    /// Constructs the BrowserService from a populated <see cref="BrowserConfiguration"/> and the
    /// default Puppeteer launcher (downloads Chromium on first use).
    /// </summary>
    public BrowserService(BrowserConfiguration browserConfig, IProxyManager proxyManager, BrowserRateLimiter rateLimiter)
    {
        _browserConfig = browserConfig;
        _proxyManager = proxyManager;
        _rateLimiter = rateLimiter;
        _launcher = options => Puppeteer.LaunchAsync(options);
        _ensureChromium = async () => { BrowserFetcher f = new BrowserFetcher(); await f.DownloadAsync(); };
        _initialStartupDelayMs = _browserConfig.InitialStartupDelayMs;
    }

    /// <summary>
    /// For testing: allows injecting a custom browser launcher and chromium ensure delegate.
    /// </summary>
    public BrowserService(BrowserConfiguration browserConfig, IProxyManager proxyManager, BrowserRateLimiter rateLimiter,
            Func<LaunchOptions, Task<IBrowser>> launcher,
            Func<Task> ensureChromium)
    {
        _browserConfig = browserConfig;
        _proxyManager = proxyManager;
        _rateLimiter = rateLimiter;
        _launcher = launcher;
        _ensureChromium = ensureChromium;
        _initialStartupDelayMs = _browserConfig.InitialStartupDelayMs;
    }

    /// <summary>
    /// Ensures Chromium is available and proxies are initialized (idempotent).
    /// </summary>
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true; // mark before awaiting to avoid re-entry
        }

        // Ensure Chromium present (downloads if missing)
        await _ensureChromium();

        // Load proxies from optional configured file; always ensures a "default" key
        string? proxyFile = _browserConfig.ProxyListPath; // optional
        await _proxyManager.InitializeAsync(proxyFile, cancellationToken);
    }

    /// <summary>
    /// Returns an existing Chromium instance for the given proxy or launches a new one with that proxy configured.
    /// One browser per proxy endpoint is cached.
    /// </summary>
    private async Task<IBrowser> GetBrowserForProxyAsync(ProxyEntry proxy, CancellationToken ct)
    {
        if (_browsers.TryGetValue(proxy.Key, out BrowserHolder? holder) && !holder.Browser.IsClosed)
            return holder.Browser;

        BrowserHolder newHolder = new BrowserHolder { Proxy = proxy };

        // Base Chromium args; keep minimal for compatibility in constrained envs.
        // --disable-blink-features=AutomationControlled removes the most obvious automation tell.
        List<string> args = new List<string> { "--no-sandbox", "--disable-dev-shm-usage", "--disable-blink-features=AutomationControlled" };
        string proxyArg = proxy.ToProxyServerArg();
        if (!string.IsNullOrWhiteSpace(proxyArg)) args.Add(proxyArg);

        // Configure Chromium launch; headless by default with a reasonable startup timeout
        LaunchOptions launchOptions = new LaunchOptions
        {
            Headless = true,
            Args = args.ToArray(),
            Timeout = 30000
        };

        // Drive a real, installed Chrome when configured (authentic TLS/HTTP-2 fingerprint, no upkeep).
        if (!string.IsNullOrWhiteSpace(_browserConfig.ChromeExecutablePath))
            launchOptions.ExecutablePath = _browserConfig.ChromeExecutablePath;

        // Optional initial delay: if configured, throttle initial browser startups relative to app start
        if (_initialStartupDelayMs > 0)
        {
            DateTime target = _appStartUtc.AddMilliseconds(_initialStartupDelayMs);
            TimeSpan remaining = target - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, ct);
            }
        }

        IBrowser browser = await _launcher(launchOptions);
        newHolder.Browser = browser;
        _browsers[proxy.Key] = newHolder;
        return browser;
    }

    /// <summary>
    /// Yields proxies in a simple round-robin order across current keys, starting from an incrementing index.
    /// </summary>
    private IEnumerable<ProxyEntry> GetRotationOrder()
    {
        IReadOnlyList<string> keys = _proxyManager.GetProxyKeys();
        if (keys.Count == 0) yield break;

        // start at round-robin index and wrap
        int start = Interlocked.Increment(ref _roundRobinIndex);
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[(start + i) % keys.Count];
            ProxyEntry? p = _proxyManager.GetProxy(key);
            if (p != null) yield return p;
        }
    }

    /// <summary>
    /// Returns the default requests-per-minute per IP. Falls back to 60 if not configured or invalid.
    /// </summary>
    private int GetDefaultRpm()
    {
        return _browserConfig.DefaultMaxRequestsPerMinutePerIp > 0 ? _browserConfig.DefaultMaxRequestsPerMinutePerIp : 60;
    }

    /// <summary>
    /// Acquires a new Puppeteer page according to options:
    /// - Chooses proxy (preferred or round-robin) and enforces per-IP RPM via BrowserRateLimiter
    /// - Applies stealth tweaks and optional network optimization (block images/fonts/css/media)
    /// - Sets user agent (override or generated)
    /// - Optionally navigates to InitialUrl with timeout
    /// </summary>
    public async Task<IPage> AcquirePageAsync(BrowserRequestOptions options, CancellationToken cancellationToken = default)
    {
        await InitAsync(cancellationToken);

        ProxyEntry proxy;
        if (!string.IsNullOrWhiteSpace(options.PreferredProxyKey))
        {
            proxy = _proxyManager.GetProxy(options.PreferredProxyKey) ?? _proxyManager.GetProxy("default")!;
        }
        else
        {
            proxy = GetRotationOrder().FirstOrDefault() ?? _proxyManager.GetProxy("default")!;
        }

        // Apply per-proxy rate limiting (by endpoint)
        int rpm = options.MaxRequestsPerMinutePerIp ?? GetDefaultRpm();
        await _rateLimiter.EnsureAllowedAsync(proxy.Endpoint, options.ConsumerKey, rpm, cancellationToken);

        IBrowser browser = await GetBrowserForProxyAsync(proxy, cancellationToken);
        IPage? page = await browser.NewPageAsync();

        // Stealth-like tweaks: avoid easy bot detections
        if (_browserConfig.StealthEnabled)
        {
            await ApplyStealthAsync(page);
        }

        // User agent: prefer explicit override, otherwise use a generated modern Chrome UA
        if (!string.IsNullOrWhiteSpace(options.UserAgentOverride))
        {
            await page.SetUserAgentAsync(options.UserAgentOverride);
        }
        else
        {
            await page.SetUserAgentAsync(UserAgentGenerator.GenerateChrome());
        }

        // Network optimization and proxy authentication per best practice
        // - Use --proxy-server at browser launch (already configured)
        // - Authenticate via page.Authenticate when credentials are present (no Proxy-Authorization header)
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            await page.AuthenticateAsync(new Credentials { Username = proxy.Username, Password = proxy.Password ?? string.Empty });
        }

        if (options.OptimizeNetwork)
        {
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (_, e) =>
            {
                IRequest? r = e.Request;
                try
                {
                    if (r.ResourceType == ResourceType.Image || r.ResourceType == ResourceType.Font || r.ResourceType == ResourceType.StyleSheet || r.ResourceType == ResourceType.Media)
                    {
                        await r.AbortAsync();
                    }
                    else
                    {
                        await r.ContinueAsync();
                    }
                }
                catch
                {
                    try { await r.ContinueAsync(); } catch { }
                }
            };
        }

        // Optional initial navigation
        if (!string.IsNullOrWhiteSpace(options.InitialUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            int to = options.NavigationTimeoutMs ?? 30000;
            try
            {
                await page.GoToAsync(options.InitialUrl!, new NavigationOptions { Timeout = to, WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });
            }
            catch (OperationCanceledException)
            {
                // Gracefully handle cancellation during shutdown
                try { await page.CloseAsync(); } catch { }
                throw;
            }
            catch (Exception ex) when (ex.Message.Contains("detached") || ex.Message.Contains("Target.detachedFromTarget"))
            {
                // Handle frame detachment during application shutdown - this is expected behavior
                // when the browser is being closed while navigation is in progress
                try { await page.CloseAsync(); } catch { }

                // Check if cancellation was requested, if so treat as cancellation rather than navigation error
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Navigation cancelled due to detached frame during shutdown", ex, cancellationToken);
                }

                // Otherwise, wrap with context so callers can attribute failures to proxy vs target
                throw new ProxyNavigationException(
                    message: $"Initial navigation failed for {options.InitialUrl}: {ex.Message}",
                    inner: ex,
                    proxyKey: proxy.Key,
                    proxyEndpoint: proxy.Endpoint,
                    url: options.InitialUrl!);
            }
            catch (Exception ex)
            {
                // Wrap with context so callers can attribute failures to proxy vs target
                throw new ProxyNavigationException(
                    message: $"Initial navigation failed for {options.InitialUrl}: {ex.Message}",
                    inner: ex,
                    proxyKey: proxy.Key,
                    proxyEndpoint: proxy.Endpoint,
                    url: options.InitialUrl!);
            }
        }

        return page;
    }

    /// <summary>
    /// Acquires multiple pages, distributing them across proxies in rotation when available.
    /// </summary>
    public async Task<IReadOnlyList<IPage>> AcquirePagesAsync(int count, BrowserRequestOptions options, CancellationToken cancellationToken = default)
    {
        if (count <= 1) return new[] { await AcquirePageAsync(options, cancellationToken) };
        List<IPage> pages = new List<IPage>(count);

        // Distribute across proxies in rotation
        ProxyEntry[] rotation = GetRotationOrder().ToArray();
        if (rotation.Length == 0) rotation = new[] { _proxyManager.GetProxy("default")! };

        for (int i = 0; i < count; i++)
        {
            string proxyKey = rotation[i % rotation.Length].Key;
            BrowserRequestOptions opts = options with { PreferredProxyKey = proxyKey };
            pages.Add(await AcquirePageAsync(opts, cancellationToken));
        }
        return pages;
    }

    /// <summary>
    /// Applies a small set of stealth tweaks inspired by puppeteer-extra-plugin-stealth to reduce automation signals.
    /// </summary>
    private async Task ApplyStealthAsync(IPage page)
    {
        // 1) Navigator properties — the full puppeteer-extra-stealth evasion set (with toString masking).
        await page.EvaluateExpressionOnNewDocumentAsync(StealthScripts.NavigatorEvasions);

        // 2) Timezone and locale emulation
        try { await page.EmulateTimezoneAsync(_browserConfig.DefaultTimezone); } catch {}
        try { await page.SetExtraHttpHeadersAsync(new Dictionary<string, string> { { "Accept-Language", _browserConfig.DefaultLocale + ",en;q=0.9" } }); } catch {}

        // 3) Viewport tweaks
        try { await page.SetViewportAsync(new ViewPortOptions { Width = 1366, Height = 768, DeviceScaleFactor = 1, IsMobile = false, HasTouch = false, IsLandscape = true }); } catch {}
    }

    /// <summary>
    /// Closes all cached browsers synchronously (best-effort) and clears cache.
    /// </summary>
    public void Dispose()
    {
        foreach (BrowserHolder b in _browsers.Values)
        {
            try { if (!b.Browser.IsClosed) b.Browser.CloseAsync().GetAwaiter().GetResult(); } catch { }
        }
        _browsers.Clear();
    }

    /// <summary>
    /// Closes all cached browsers asynchronously (best-effort) and clears cache.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (BrowserHolder b in _browsers.Values)
        {
            try { if (!b.Browser.IsClosed) await b.Browser.CloseAsync(); } catch { }
        }
        _browsers.Clear();
    }
}
