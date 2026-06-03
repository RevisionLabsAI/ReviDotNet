// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Revi;

/// <summary>
/// Default <see cref="IWebContentService"/>. Orchestrates the pipeline:
/// <c>IWebFetcher → IContentExtractor → (IMarkdownConverter + IMetadataExtractor) → IContentChunker</c>,
/// merging Readability's recovered fields under the structured-metadata ladder. <see cref="CrawlAsync"/>
/// adds the crawl infrastructure — per-domain round-robin queue, dedup, AutoThrottle, robots/Crawl-delay,
/// link discovery, and bounded concurrency — streaming documents as each page completes. The injected
/// <see cref="IWebFetcher"/> may be a tiered escalator (HTTP → browser) once <c>ReviDotNet.Scraping</c>
/// is registered; this orchestrator is agnostic to which fetcher served a page.
/// </summary>
public sealed class WebContentService : IWebContentService
{
    private readonly IWebFetcher _fetcher;
    private readonly IContentExtractor _extractor;
    private readonly IMarkdownConverter _markdown;
    private readonly IMetadataExtractor _metadata;
    private readonly IContentChunker _chunker;
    private readonly IReviLogger<WebContentService>? _logger;
    private readonly IWebContentCache? _cache;

    /// <summary>Robots user-agent token used for crawl politeness checks.</summary>
    private const string RobotsUserAgent = "*";

    /// <summary>Creates the service from its pipeline stages. Logger and cache are optional (null disables each).</summary>
    public WebContentService(
        IWebFetcher fetcher,
        IContentExtractor extractor,
        IMarkdownConverter markdown,
        IMetadataExtractor metadata,
        IContentChunker chunker,
        IReviLogger<WebContentService>? logger = null,
        IWebContentCache? cache = null)
    {
        _fetcher = fetcher;
        _extractor = extractor;
        _markdown = markdown;
        _metadata = metadata;
        _chunker = chunker;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Builds a fully-default pipeline (HTTP fetcher + Readability + ReverseMarkdown + structured
    /// metadata + heading/token chunker) with no DI. Used by the built-in web tools when constructed
    /// outside the container.
    /// </summary>
    public static WebContentService CreateDefault()
        => new(
            new HttpWebFetcher(),
            new ReadabilityContentExtractor(),
            new ReverseMarkdownConverter(),
            new StructuredDataMetadataExtractor(),
            new HeadingTokenChunker());

    /// <inheritdoc/>
    public async Task<WebDocument> FetchAsync(string url, WebFetchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new WebFetchOptions();

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? requestedUri))
            throw new ArgumentException($"Invalid absolute URL: '{url}'", nameof(url));

        string cacheKey = UrlCanonicalizer.Canonicalize(url) + (options.Chunk ? "|chunk" : string.Empty);
        if (_cache is not null && _cache.TryGet(cacheKey, out WebDocument cached))
        {
            _logger?.LogDebug($"web-fetch cache hit: {url}");
            return cached;
        }

        FetchResult fetch = await _fetcher.FetchAsync(BuildFetchRequest(url, options), cancellationToken);
        WebDocument doc = BuildDocument(fetch, options, requestedUri);

        _logger?.LogDebug($"web-fetch {doc.Url} → tier={doc.FetchInfo.Tier}, status={doc.FetchInfo.StatusCode}, " +
                          $"{doc.FetchInfo.ElapsedMs}ms, blocked={doc.FetchInfo.Blocked}, md={doc.Markdown.Length} chars");

        if (_cache is not null && !doc.FetchInfo.Blocked && doc.Markdown.Length > 0)
            _cache.Set(cacheKey, doc);

        return doc;
    }

    /// <summary>Maps per-call options to a fetcher request.</summary>
    private static FetchRequest BuildFetchRequest(string url, WebFetchOptions options) => new()
    {
        Url = url,
        RenderMode = options.RenderMode,
        MaxTier = options.MaxTier,
        UserAgent = options.UserAgent,
        Headers = options.Headers,
        RespectRobots = options.RespectRobots,
        TimeoutMs = options.TimeoutMs,
    };

    /// <summary>Runs the extraction pipeline over an already-fetched result and assembles a <see cref="WebDocument"/>.</summary>
    private WebDocument BuildDocument(FetchResult fetch, WebFetchOptions options, Uri requestedUri)
    {
        Uri baseUri = Uri.TryCreate(fetch.FinalUrl, UriKind.Absolute, out Uri? finalUri) ? finalUri : requestedUri;

        string html = fetch.Html ?? string.Empty;
        if (options.MaxContentLength > 0 && html.Length > options.MaxContentLength)
            html = html[..options.MaxContentLength];

        ExtractedContent extracted = _extractor.Extract(html, baseUri);
        WebMetadata meta = _metadata.Extract(html, baseUri);
        string markdown = _markdown.ToMarkdown(extracted.ContentHtml, baseUri);

        IReadOnlyList<WebChunk> chunks = options.Chunk
            ? _chunker.Chunk(markdown, meta, options.ChunkOptions)
            : [];

        // Title: the structured ladder (JSON-LD/OG/Twitter/<title>) is authoritative; fall back to
        // Readability's title, then strip a trailing/leading site-name suffix (e.g. "Post | Site").
        string? siteName = meta.SiteName ?? extracted.SiteName;
        string? title = StripSiteSuffix(meta.Title ?? extracted.Title, siteName);

        return new WebDocument
        {
            Url = fetch.FinalUrl,
            CanonicalUrl = meta.CanonicalUrl,
            Title = title,
            Author = meta.Author ?? extracted.Author,
            PublishedAt = meta.PublishedAt ?? extracted.PublishedAt,
            ModifiedAt = meta.ModifiedAt,
            Description = meta.Description ?? extracted.Excerpt,
            Language = meta.Language ?? extracted.Language,
            SiteName = siteName,
            Tags = meta.Tags,
            LeadImageUrl = meta.LeadImageUrl ?? extracted.LeadImageUrl,
            Markdown = markdown,
            Chunks = chunks,
            FetchInfo = new WebFetchInfo
            {
                Tier = fetch.Tier,
                StatusCode = fetch.StatusCode,
                ElapsedMs = fetch.ElapsedMs,
                ContentType = fetch.ContentType,
                RawLength = (fetch.Html ?? string.Empty).Length,
                Blocked = fetch.Blocked,
                Note = fetch.Note,
            },
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<WebDocument> CrawlAsync(
        WebCrawlRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<WebDocument> channel = Channel.CreateUnbounded<WebDocument>(
            new UnboundedChannelOptions { SingleReader = true });

        Task producer = RunCrawlAsync(request, channel.Writer, cancellationToken);

        try
        {
            await foreach (WebDocument doc in channel.Reader.ReadAllAsync(cancellationToken))
                yield return doc;
        }
        finally
        {
            await producer; // surface producer faults and ensure completion
        }
    }

    /// <summary>
    /// The crawl producer: seeds the frontier, then runs a bounded worker pool that dequeues
    /// (per-domain round-robin), checks robots, throttles (AutoThrottle + Crawl-delay), fetches,
    /// writes the document, and enqueues discovered same-site links until the page or depth budget runs out.
    /// </summary>
    private async Task RunCrawlAsync(WebCrawlRequest request, ChannelWriter<WebDocument> writer, CancellationToken ct)
    {
        Exception? fault = null;
        try
        {
            RequestQueue queue = new();
            InMemoryDupeFilter dupe = new();
            DomainThrottle throttle = new();
            RobotsTxtCache robots = new(RobotsUserAgent);
            HashSet<string> sites = new(StringComparer.OrdinalIgnoreCase);

            bool respectRobots = request.FetchOptions.RespectRobots;
            int maxPages = Math.Max(1, request.MaxPages);
            int maxDepth = Math.Max(0, request.MaxDepth);

            long pending = 0;
            long fetched = 0;

            void Enqueue(string url, int depth)
            {
                Interlocked.Increment(ref pending);
                queue.Enqueue(new RequestQueue.CrawlItem(url, depth));
            }

            foreach (string seed in request.SeedUrls)
            {
                if (!Uri.TryCreate(seed, UriKind.Absolute, out _)) continue;
                if (request.UrlFilter is not null && !request.UrlFilter(seed)) continue;
                if (dupe.TryAdd(seed))
                {
                    sites.Add(UrlCanonicalizer.SiteKey(seed));
                    Enqueue(seed, 0);
                }
            }

            async Task Worker()
            {
                while (!ct.IsCancellationRequested)
                {
                    if (Interlocked.Read(ref pending) == 0) break;
                    if (!queue.TryDequeue(out RequestQueue.CrawlItem item))
                    {
                        await Task.Delay(10, ct);
                        continue;
                    }

                    try
                    {
                        if (Interlocked.Read(ref fetched) >= maxPages) continue; // budget reached

                        Uri uri = new(item.Url);
                        if (respectRobots && !await robots.IsAllowedAsync(uri, ct))
                        {
                            _logger?.LogDebug($"crawl: robots.txt disallows {item.Url}");
                            continue;
                        }

                        string host = uri.Host;
                        if (respectRobots)
                            throttle.SetCrawlDelay(host, await robots.GetCrawlDelayAsync(uri, ct));

                        await throttle.AcquireAsync(host, ct);
                        double latencyMs = 0;
                        bool ok = false;
                        try
                        {
                            FetchResult fetch = await _fetcher.FetchAsync(BuildFetchRequest(item.Url, request.FetchOptions), ct);
                            latencyMs = fetch.ElapsedMs;
                            ok = !fetch.Blocked && fetch.StatusCode is >= 200 and < 400;

                            long n = Interlocked.Increment(ref fetched);
                            if (n <= maxPages)
                            {
                                WebDocument doc = BuildDocument(fetch, request.FetchOptions, uri);
                                await writer.WriteAsync(doc, ct);

                                if (item.Depth < maxDepth && !string.IsNullOrEmpty(fetch.Html) &&
                                    Interlocked.Read(ref fetched) < maxPages)
                                {
                                    Uri baseUri = Uri.TryCreate(fetch.FinalUrl, UriKind.Absolute, out Uri? fu) ? fu : uri;
                                    foreach (string link in LinkExtractor.ExtractLinks(fetch.Html, baseUri))
                                    {
                                        if (request.SameSiteOnly && !sites.Contains(UrlCanonicalizer.SiteKey(link))) continue;
                                        if (request.UrlFilter is not null && !request.UrlFilter(link)) continue;
                                        if (dupe.TryAdd(link)) Enqueue(link, item.Depth + 1);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            throttle.Release(host, latencyMs > 0 ? latencyMs : 1, ok);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"crawl: error processing {item.Url}: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref pending);
                    }
                }
            }

            int workerCount = Math.Max(1, request.MaxConcurrency);
            Task[] workers = new Task[workerCount];
            for (int i = 0; i < workerCount; i++)
                workers[i] = Task.Run(() => Worker(), ct);

            await Task.WhenAll(workers);
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            writer.TryComplete(fault);
        }
    }

    /// <summary>Separators commonly used between a page title and its site name.</summary>
    private static readonly string[] TitleSeparators = [" | ", " - ", " — ", " – ", " · ", " :: ", " » "];

    /// <summary>
    /// Conservatively strips a trailing (or leading) " &lt;sep&gt; SiteName" from a title, but only when
    /// it exactly matches the known site name — so it never over-trims a legitimate title.
    /// </summary>
    private static string? StripSiteSuffix(string? title, string? siteName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(siteName)) return title;
        string t = title.Trim();
        string site = siteName.Trim();

        foreach (string sep in TitleSeparators)
        {
            string suffix = sep + site;
            if (t.Length > suffix.Length && t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return t[..^suffix.Length].Trim();

            string prefix = site + sep;
            if (t.Length > prefix.Length && t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return t[prefix.Length..].Trim();
        }
        return t;
    }
}
