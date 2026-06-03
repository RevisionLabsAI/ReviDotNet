// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using AngleSharp.Html.Parser;

namespace Revi;

/// <summary>
/// Main-content extractor built on <c>SmartReader</c> (a direct port of Mozilla's Readability
/// algorithm on AngleSharp). Scores DOM nodes to find the article container, strips boilerplate, and
/// recovers title/author/date/site/lead-image/reading-time. Falls back to a conservative body cleanup
/// when the page is not "readerable", so the pipeline almost never returns empty.
/// </summary>
public sealed class ReadabilityContentExtractor : IContentExtractor
{
    /// <summary>Tags whose subtrees are removed in the non-readerable fallback path.</summary>
    private static readonly string[] FallbackStripSelectors =
        ["script", "style", "noscript", "nav", "footer", "header", "aside", "form", "iframe", "svg"];

    /// <inheritdoc/>
    public ExtractedContent Extract(string html, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new ExtractedContent { ContentHtml = string.Empty, IsReadable = false, TextLength = 0 };

        try
        {
            using SmartReader.Reader reader = new(baseUrl.AbsoluteUri, html);
            SmartReader.Article article = reader.GetArticle();

            if (article is { IsReadable: true } && !string.IsNullOrWhiteSpace(article.Content))
            {
                int textLen = (article.TextContent ?? string.Empty).Trim().Length;
                return new ExtractedContent
                {
                    ContentHtml = article.Content!,
                    Title = NullIfBlank(article.Title),
                    Author = NullIfBlank(article.Author),
                    Excerpt = NullIfBlank(article.Excerpt),
                    SiteName = NullIfBlank(article.SiteName),
                    Language = NullIfBlank(article.Language),
                    PublishedAt = ToOffset(article.PublicationDate),
                    LeadImageUrl = NullIfBlank(article.FeaturedImage),
                    TimeToReadMinutes = article.TimeToRead.TotalMinutes >= 1
                        ? (int)Math.Ceiling(article.TimeToRead.TotalMinutes)
                        : null,
                    TextLength = textLen,
                    IsReadable = true,
                };
            }
        }
        catch
        {
            // Fall through to the conservative fallback below.
        }

        return Fallback(html);
    }

    /// <summary>
    /// Conservative fallback when Readability declines a page: strip obvious boilerplate elements and
    /// return the body HTML, so the Markdown converter still has something usable.
    /// </summary>
    private static ExtractedContent Fallback(string html)
    {
        try
        {
            HtmlParser parser = new();
            AngleSharp.Html.Dom.IHtmlDocument doc = parser.ParseDocument(html);

            foreach (string selector in FallbackStripSelectors)
            {
                foreach (AngleSharp.Dom.IElement el in doc.QuerySelectorAll(selector))
                    el.Remove();
            }

            string contentHtml = doc.Body?.InnerHtml ?? html;
            int textLen = (doc.Body?.TextContent ?? string.Empty).Trim().Length;
            return new ExtractedContent
            {
                ContentHtml = contentHtml,
                Title = NullIfBlank(doc.Title),
                TextLength = textLen,
                IsReadable = false,
            };
        }
        catch
        {
            return new ExtractedContent { ContentHtml = html, IsReadable = false, TextLength = html.Length };
        }
    }

    /// <summary>Returns null for null/whitespace strings, otherwise the trimmed value.</summary>
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Converts a possibly kind-unspecified <see cref="DateTime"/> to a UTC-anchored offset without throwing.</summary>
    private static DateTimeOffset? ToOffset(DateTime? dt)
    {
        if (dt is not DateTime d) return null;
        DateTime kinded = d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d;
        return new DateTimeOffset(kinded);
    }
}
