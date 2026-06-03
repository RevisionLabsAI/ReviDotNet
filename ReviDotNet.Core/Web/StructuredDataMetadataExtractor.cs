// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// Extracts page metadata via the precedence ladder JSON-LD → OpenGraph → Twitter Cards → standard
/// <c>&lt;meta&gt;</c>/<c>&lt;title&gt;</c>/<c>&lt;link rel=canonical&gt;</c>/<c>&lt;html lang&gt;</c> →
/// DOM heuristics. JSON-LD (schema.org Article family) wins where present; the canonical URL prefers
/// <c>rel=canonical</c> over <c>og:url</c>. URLs are resolved to absolute against the page base.
/// </summary>
public sealed class StructuredDataMetadataExtractor : IMetadataExtractor
{
    /// <summary>schema.org <c>@type</c> values treated as primary article-like content.</summary>
    private static readonly HashSet<string> ArticleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Article", "NewsArticle", "BlogPosting", "Report", "TechArticle", "ScholarlyArticle",
        "AdvertiserContentArticle", "WebPage", "ItemPage", "AboutPage", "FAQPage",
    };

    /// <inheritdoc/>
    public WebMetadata Extract(string html, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return new WebMetadata();

        IHtmlDocument doc;
        try
        {
            doc = new HtmlParser().ParseDocument(html);
        }
        catch
        {
            return new WebMetadata();
        }

        JObject? jsonLd = FindArticleJsonLd(doc, out string? jsonLdSiteName);

        // OpenGraph / Twitter / standard meta — read lazily via helpers below.
        string? title = First(
            JsonString(jsonLd, "headline"), JsonString(jsonLd, "name"),
            MetaContent(doc, "property", "og:title"),
            MetaContent(doc, "name", "twitter:title"),
            doc.Title);

        string? description = First(
            JsonString(jsonLd, "description"),
            MetaContent(doc, "property", "og:description"),
            MetaContent(doc, "name", "twitter:description"),
            MetaContent(doc, "name", "description"));

        string? author = First(
            JsonAuthor(jsonLd?["author"]),
            MetaContent(doc, "name", "author"),
            MetaContent(doc, "property", "article:author"),
            doc.QuerySelector("[rel=author]")?.TextContent?.Trim());

        string? siteName = First(
            jsonLdSiteName,
            MetaContent(doc, "property", "og:site_name"),
            MetaContent(doc, "name", "application-name"));

        string? language = First(
            JsonLanguage(jsonLd?["inLanguage"]),
            doc.DocumentElement?.GetAttribute("lang"),
            MetaContent(doc, "property", "og:locale"),
            MetaContent(doc, "http-equiv", "content-language"));

        string? leadImage = Abs(baseUrl, First(
            JsonImage(jsonLd?["image"]),
            MetaContent(doc, "property", "og:image"),
            MetaContent(doc, "name", "twitter:image"),
            MetaContent(doc, "name", "twitter:image:src")));

        // Canonical: rel=canonical is the authority, then og:url, then JSON-LD url.
        string? canonical = Abs(baseUrl, First(
            doc.QuerySelector("link[rel=canonical]")?.GetAttribute("href"),
            MetaContent(doc, "property", "og:url"),
            JsonString(jsonLd, "url")));

        DateTimeOffset? published = ParseDate(First(
            JsonString(jsonLd, "datePublished"),
            MetaContent(doc, "property", "article:published_time"),
            MetaContent(doc, "name", "date"),
            MetaContent(doc, "name", "pubdate"),
            doc.QuerySelector("time[datetime]")?.GetAttribute("datetime")));

        DateTimeOffset? modified = ParseDate(First(
            JsonString(jsonLd, "dateModified"),
            MetaContent(doc, "property", "article:modified_time"),
            MetaContent(doc, "property", "og:updated_time"),
            MetaContent(doc, "name", "lastmod")));

        return new WebMetadata
        {
            Title = NullIfBlank(title),
            Author = NullIfBlank(author),
            Description = NullIfBlank(description),
            SiteName = NullIfBlank(siteName),
            Language = NormalizeLang(language),
            LeadImageUrl = NullIfBlank(leadImage),
            CanonicalUrl = NullIfBlank(canonical),
            PublishedAt = published,
            ModifiedAt = modified,
            Tags = CollectTags(doc, jsonLd),
        };
    }

    // ---- JSON-LD ----------------------------------------------------------------------------------

    /// <summary>Scans all JSON-LD blocks (expanding <c>@graph</c>) for the first article-like object; also reports a publisher/site name.</summary>
    private static JObject? FindArticleJsonLd(IHtmlDocument doc, out string? siteName)
    {
        siteName = null;
        JObject? article = null;
        foreach (IElement script in doc.QuerySelectorAll("script[type=\"application/ld+json\"]"))
        {
            string raw = script.TextContent;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JToken root;
            try { root = JToken.Parse(raw); }
            catch { continue; }

            foreach (JObject obj in EnumerateObjects(root))
            {
                string? type = TypeOf(obj);
                if (siteName is null && (string.Equals(type, "Organization", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(type, "WebSite", StringComparison.OrdinalIgnoreCase)))
                    siteName = obj["name"]?.ToString();

                if (article is null && IsArticleType(obj["@type"]))
                {
                    article = obj;
                    // publisher/site name often lives on the article
                    siteName ??= obj["publisher"]?["name"]?.ToString() ?? obj["publisher"]?.ToString();
                }
            }
        }
        return article;
    }

    /// <summary>Yields every JObject reachable from a JSON-LD root, descending into arrays and <c>@graph</c>.</summary>
    private static IEnumerable<JObject> EnumerateObjects(JToken token)
    {
        switch (token)
        {
            case JArray array:
                foreach (JToken item in array)
                    foreach (JObject o in EnumerateObjects(item))
                        yield return o;
                break;
            case JObject obj:
                yield return obj;
                if (obj["@graph"] is JToken graph)
                    foreach (JObject o in EnumerateObjects(graph))
                        yield return o;
                break;
        }
    }

    /// <summary>Returns the first <c>@type</c> as a string (handles array-valued types).</summary>
    private static string? TypeOf(JObject obj)
    {
        JToken? t = obj["@type"];
        if (t is null) return null;
        return t.Type == JTokenType.Array ? t.First?.ToString() : t.ToString();
    }

    /// <summary>Whether a <c>@type</c> token (string or array) names an article-like type.</summary>
    private static bool IsArticleType(JToken? t)
    {
        if (t is null) return false;
        IEnumerable<string> types = t.Type == JTokenType.Array
            ? t.Select(x => x.ToString())
            : [t.ToString()];
        return types.Any(ArticleTypes.Contains);
    }

    /// <summary>Reads a string-valued JSON-LD property off the article object.</summary>
    private static string? JsonString(JObject? obj, string prop)
    {
        JToken? t = obj?[prop];
        return t is null || t.Type is JTokenType.Object or JTokenType.Array ? null : t.ToString();
    }

    /// <summary>Resolves a JSON-LD <c>author</c> (string, object with name, or array thereof) to a display string.</summary>
    private static string? JsonAuthor(JToken? a)
    {
        if (a is null) return null;
        switch (a.Type)
        {
            case JTokenType.Array:
                List<string> names = [];
                foreach (JToken x in a)
                {
                    string? n = JsonAuthor(x);
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
                }
                return names.Count > 0 ? string.Join(", ", names) : null;
            case JTokenType.Object:
                return a["name"]?.ToString();
            case JTokenType.String:
                return a.ToString();
            default:
                return null;
        }
    }

    /// <summary>Resolves a JSON-LD <c>image</c> (string, object with url, or array) to a single URL.</summary>
    private static string? JsonImage(JToken? im)
    {
        if (im is null) return null;
        return im.Type switch
        {
            JTokenType.Array => JsonImage(im.First),
            JTokenType.Object => im["url"]?.ToString() ?? im["contentUrl"]?.ToString(),
            JTokenType.String => im.ToString(),
            _ => null,
        };
    }

    /// <summary>Resolves a JSON-LD <c>inLanguage</c> (string or Language object) to a tag.</summary>
    private static string? JsonLanguage(JToken? l)
    {
        if (l is null) return null;
        return l.Type == JTokenType.Object ? l["name"]?.ToString() ?? l["alternateName"]?.ToString() : l.ToString();
    }

    // ---- Tags -------------------------------------------------------------------------------------

    /// <summary>Collects de-duplicated tags from JSON-LD keywords, OG <c>article:tag</c>, and meta keywords.</summary>
    private static IReadOnlyList<string> CollectTags(IHtmlDocument doc, JObject? jsonLd)
    {
        List<string> tags = [];
        void AddCsvOrSingle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tags.Add(part);
        }

        JToken? keywords = jsonLd?["keywords"];
        if (keywords is JArray arr)
            foreach (JToken k in arr) AddCsvOrSingle(k?.ToString());
        else
            AddCsvOrSingle(keywords?.ToString());

        foreach (IElement tag in doc.QuerySelectorAll("meta[property=\"article:tag\"]"))
            AddCsvOrSingle(tag.GetAttribute("content"));

        AddCsvOrSingle(MetaContent(doc, "name", "keywords"));

        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- DOM / meta helpers -----------------------------------------------------------------------

    /// <summary>Reads the <c>content</c> of a <c>&lt;meta&gt;</c> matched by an attribute/value pair.</summary>
    private static string? MetaContent(IHtmlDocument doc, string attribute, string value)
        => doc.QuerySelector($"meta[{attribute}=\"{value}\"]")?.GetAttribute("content");

    /// <summary>Returns the first non-blank value, trimmed; null if all are blank.</summary>
    private static string? First(params string?[] values)
    {
        foreach (string? v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    /// <summary>Resolves a possibly-relative URL to absolute against the base; passes through if blank/unparseable.</summary>
    private static string? Abs(Uri baseUrl, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(baseUrl, value.Trim(), out Uri? abs) ? abs.ToString() : value.Trim();
    }

    /// <summary>Parses a metadata date string into a UTC-anchored offset; null on failure.</summary>
    private static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset dto)
            ? dto
            : null;
    }

    /// <summary>Normalizes an OG-style locale (e.g. <c>en_US</c>) to a BCP-47-ish tag.</summary>
    private static string? NormalizeLang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        return lang.Trim().Replace('_', '-');
    }

    /// <summary>Returns null for null/whitespace, otherwise the trimmed value.</summary>
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
