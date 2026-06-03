// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Verifies the Phase 1 LLM-ready extraction pipeline: Readability content extraction, HTML→Markdown
/// conversion (relative-URL resolution + complex-table-as-HTML), structured metadata, heading/token
/// chunking, and the end-to-end <see cref="WebContentService"/> over a fake fetcher (no network).
/// </summary>
public class WebExtractionTests
{
    private static readonly Uri Base = new("https://example.com/blog/post");

    // ---- Content extraction + Markdown conversion -------------------------------------------------

    [Fact]
    public void Extractor_And_Converter_KeepArticle_DropBoilerplate()
    {
        string html = """
            <html><head><title>My Great Post | Example Blog</title></head>
            <body>
              <nav><a href="/a">Home</a> <a href="/b">About</a> <a href="/c">Contact</a></nav>
              <article>
                <h1>My Great Post</h1>
                <p>This is the first substantial paragraph of the article, with several clauses,
                   commas, and enough length that the readability scorer treats it as real content
                   rather than boilerplate navigation chrome.</p>
                <p>Here is a second paragraph that continues the discussion in depth, again with
                   commas and a reasonable amount of prose so the container scores highly.</p>
                <p>See the <a href="/related">related guide</a> for more details about the topic.</p>
              </article>
              <footer>Copyright NoiseCorp 2026 — unrelated footer boilerplate links everywhere.</footer>
            </body></html>
            """;

        var extractor = new ReadabilityContentExtractor();
        var converter = new ReverseMarkdownConverter();

        ExtractedContent extracted = extractor.Extract(html, Base);
        string markdown = converter.ToMarkdown(extracted.ContentHtml, Base);

        markdown.Should().Contain("first substantial paragraph");
        markdown.Should().Contain("second paragraph");
        markdown.Should().NotContain("NoiseCorp");          // footer dropped
        markdown.Should().NotContain("Contact");            // nav dropped
        markdown.Should().Contain("https://example.com/related"); // relative link resolved to absolute
    }

    [Fact]
    public void Converter_ResolvesRelativeUrls_AgainstBase()
    {
        var converter = new ReverseMarkdownConverter();
        string md = converter.ToMarkdown("<p><a href=\"/page?x=1\">link</a> and <img src=\"img/p.png\"></p>",
            new Uri("https://site.test/dir/index.html"));

        md.Should().Contain("https://site.test/page?x=1");
        md.Should().Contain("https://site.test/dir/img/p.png");
    }

    [Fact]
    public void Converter_SimpleTable_BecomesGfm_ComplexTable_StaysHtml()
    {
        var converter = new ReverseMarkdownConverter();

        string simple = converter.ToMarkdown(
            "<table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table>",
            Base);
        simple.Should().Contain("|"); // GFM pipe table

        string complex = converter.ToMarkdown(
            "<table><tr><td colspan=\"2\">spanning header</td></tr><tr><td>1</td><td>2</td></tr></table>",
            Base);
        complex.Should().Contain("colspan");        // kept as raw HTML
        complex.Should().Contain("<table");         // raw table element preserved
        complex.Should().NotContain("REVITABLEPLACEHOLDER"); // sentinel fully restored
    }

    // ---- Metadata ladder --------------------------------------------------------------------------

    [Fact]
    public void Metadata_JsonLd_Wins_Over_OpenGraph()
    {
        string html = """
            <html lang="en">
            <head>
              <title>Raw Title | Site</title>
              <meta property="og:title" content="OG Title">
              <meta property="og:site_name" content="Example News">
              <meta property="og:url" content="https://example.com/wrong-canonical">
              <link rel="canonical" href="https://example.com/blog/post">
              <meta name="keywords" content="alpha, beta">
              <script type="application/ld+json">
              {
                "@context":"https://schema.org",
                "@type":"NewsArticle",
                "headline":"JSON-LD Headline",
                "author":{"@type":"Person","name":"Ada Lovelace"},
                "datePublished":"2026-01-15T09:30:00Z",
                "inLanguage":"en-US",
                "image":"https://cdn.example.com/lead.jpg",
                "keywords":["gamma","delta"]
              }
              </script>
            </head><body><p>hi</p></body></html>
            """;

        var meta = new StructuredDataMetadataExtractor().Extract(html, Base);

        meta.Title.Should().Be("JSON-LD Headline");
        meta.Author.Should().Be("Ada Lovelace");
        meta.SiteName.Should().Be("Example News");
        meta.Language.Should().Be("en-US");
        meta.LeadImageUrl.Should().Be("https://cdn.example.com/lead.jpg");
        meta.CanonicalUrl.Should().Be("https://example.com/blog/post"); // rel=canonical beats og:url
        meta.PublishedAt.Should().NotBeNull();
        meta.PublishedAt!.Value.Year.Should().Be(2026);
        meta.Tags.Should().Contain("gamma");
    }

    [Fact]
    public void Metadata_FallsBackTo_OpenGraph_And_Meta()
    {
        string html = """
            <html lang="fr">
            <head>
              <title>Fallback Title</title>
              <meta property="og:title" content="OG Fallback Title">
              <meta property="og:description" content="og description here">
              <meta name="author" content="Grace Hopper">
            </head><body><p>hi</p></body></html>
            """;

        var meta = new StructuredDataMetadataExtractor().Extract(html, Base);

        meta.Title.Should().Be("OG Fallback Title");
        meta.Description.Should().Be("og description here");
        meta.Author.Should().Be("Grace Hopper");
        meta.Language.Should().Be("fr");
    }

    // ---- Chunking ---------------------------------------------------------------------------------

    [Fact]
    public void Chunker_SplitsOnHeadings_AndPrependsTrail()
    {
        string md = """
            ## Section One

            Some content under section one.

            ## Section Two

            Some content under section two.
            """;

        var chunker = new HeadingTokenChunker();
        var chunks = chunker.Chunk(md, new WebMetadata { Title = "Doc" }, new ChunkOptions());

        chunks.Count.Should().Be(2);
        chunks[0].HeadingTrail.Should().Be("Doc > Section One");
        chunks[0].Text.Should().StartWith("Doc > Section One");
        chunks[0].Text.Should().Contain("section one");
        chunks[1].HeadingTrail.Should().Be("Doc > Section Two");
    }

    [Fact]
    public void Chunker_SplitsOversizedSection_IntoMultipleChunks()
    {
        string paragraph = string.Join(" ", Enumerable.Repeat("alpha beta gamma delta epsilon zeta", 60));
        string md = "# Big\n\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 8));

        var chunker = new HeadingTokenChunker();
        var opts = new ChunkOptions { MaxTokens = 200, OverlapTokens = 30 };
        var chunks = chunker.Chunk(md, new WebMetadata { Title = "Doc" }, opts);

        chunks.Count.Should().BeGreaterThan(1);
        // Each chunk should be near or under the token budget (allow slack for the prepended trail).
        chunks.Should().OnlyContain(c => c.EstimatedTokens <= opts.MaxTokens + 80);
    }

    // ---- End-to-end pipeline over a fake fetcher --------------------------------------------------

    [Fact]
    public async Task WebContentService_EndToEnd_ProducesFrontmatterMarkdown_AndChunks()
    {
        string html = """
            <html lang="en"><head>
              <title>Pipeline Test | Example</title>
              <meta property="og:site_name" content="Example">
              <script type="application/ld+json">
              {"@context":"https://schema.org","@type":"Article","headline":"Pipeline Test",
               "author":{"name":"Tester"},"datePublished":"2026-02-02T00:00:00Z"}
              </script>
            </head><body>
              <nav><a href="/x">x</a></nav>
              <article>
                <h1>Pipeline Test</h1>
                <p>A sufficiently long paragraph of real content, with commas and clauses, so that the
                   extractor treats the article body as the main content of the page and not chrome.</p>
                <h2>Details</h2>
                <p>More content here describing the details of the pipeline test in additional prose.</p>
              </article>
            </body></html>
            """;

        var fetcher = new FakeWebFetcher(html, "https://example.com/blog/post");
        var service = new WebContentService(
            fetcher,
            new ReadabilityContentExtractor(),
            new ReverseMarkdownConverter(),
            new StructuredDataMetadataExtractor(),
            new HeadingTokenChunker());

        WebDocument doc = await service.FetchAsync("https://example.com/blog/post",
            new WebFetchOptions { Chunk = true }, CancellationToken.None);

        doc.Title.Should().Be("Pipeline Test");
        doc.Author.Should().Be("Tester");
        doc.SiteName.Should().Be("Example");
        doc.Markdown.Should().NotBeNullOrWhiteSpace();
        doc.Markdown.Should().Contain("real content");
        doc.Chunks.Should().NotBeEmpty();
        doc.FetchInfo.Tier.Should().Be(WebFetchTier.Http);

        string frontmatter = doc.ToFrontmatterMarkdown();
        frontmatter.Should().StartWith("---");
        frontmatter.Should().Contain("title: \"Pipeline Test\"");
        frontmatter.Should().Contain("site_name: \"Example\"");
    }

    [Fact]
    public async Task WebContentService_Cache_AvoidsRefetch()
    {
        string html = "<html><body><article><h1>T</h1><p>" + new string('x', 600) + "</p></article></body></html>";
        var fetcher = new CountingFetcher(html, "https://example.com/p");
        var cache = new InMemoryWebContentCache(TimeSpan.FromMinutes(5));
        var service = new WebContentService(
            fetcher,
            new ReadabilityContentExtractor(),
            new ReverseMarkdownConverter(),
            new StructuredDataMetadataExtractor(),
            new HeadingTokenChunker(),
            logger: null,
            cache: cache);

        WebDocument d1 = await service.FetchAsync("https://example.com/p", null, CancellationToken.None);
        WebDocument d2 = await service.FetchAsync("https://example.com/p", null, CancellationToken.None);

        fetcher.Calls.Should().Be(1);          // second call served from cache
        d2.Markdown.Should().Be(d1.Markdown);
    }

    /// <summary>An <see cref="IWebFetcher"/> that returns fixed HTML and counts invocations.</summary>
    private sealed class CountingFetcher(string html, string finalUrl) : IWebFetcher
    {
        public int Calls { get; private set; }
        public WebFetchTier Tier => WebFetchTier.Http;

        public Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new FetchResult
            {
                Html = html,
                FinalUrl = finalUrl,
                StatusCode = 200,
                ContentType = "text/html",
                Tier = Tier,
                ElapsedMs = 1,
            });
        }
    }

    /// <summary>A canned <see cref="IWebFetcher"/> that returns fixed HTML, for network-free pipeline tests.</summary>
    private sealed class FakeWebFetcher(string html, string finalUrl) : IWebFetcher
    {
        public WebFetchTier Tier => WebFetchTier.Http;

        public Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new FetchResult
            {
                Html = html,
                FinalUrl = finalUrl,
                StatusCode = 200,
                ContentType = "text/html",
                Tier = Tier,
                ElapsedMs = 1,
            });
    }
}
