// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Revi;

/// <summary>
/// DOM-walking HTML → Markdown converter built on <c>ReverseMarkdown.Net</c> (GFM tables, fenced code,
/// nested lists, images). Before conversion it normalizes the HTML — the real lesson from Defuddle/
/// Trafilatura is that good Markdown starts by fixing the HTML, not patching the Markdown afterward:
/// <list type="bullet">
/// <item><description>Resolve relative <c>href</c>/<c>src</c> against the page base URL (and <c>&lt;base&gt;</c>).</description></item>
/// <item><description>Keep complex tables (rowspan/colspan or nested tables, which GFM cannot express)
/// as raw inline HTML — LLMs read HTML tables fine.</description></item>
/// </list>
/// </summary>
public sealed class ReverseMarkdownConverter : IMarkdownConverter
{
    /// <summary>Sentinel prefix for stashed complex-table placeholders (pure alphanumerics so the converter never escapes it).</summary>
    private const string TablePlaceholderPrefix = "REVITABLEPLACEHOLDER";

    private static readonly ReverseMarkdown.Config MarkdownConfig = new()
    {
        GithubFlavored = true,            // GFM tables, strikethrough
        RemoveComments = true,
        SmartHrefHandling = true,          // drop redundant [text](text) links
        UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.PassThrough,
    };

    private static readonly Regex MultiBlankLine = new(@"\n{3,}", RegexOptions.Compiled);

    /// <inheritdoc/>
    public string ToMarkdown(string contentHtml, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(contentHtml)) return string.Empty;

        string preparedHtml;
        List<string> stashedTables = [];
        try
        {
            HtmlParser parser = new();
            IHtmlDocument doc = parser.ParseDocument(contentHtml);

            Uri effectiveBase = ResolveEffectiveBase(doc, baseUrl);
            ResolveUrls(doc, effectiveBase);
            StashComplexTables(doc, stashedTables);

            preparedHtml = doc.Body?.InnerHtml ?? contentHtml;
        }
        catch
        {
            // If DOM prep fails, fall back to converting the raw HTML directly.
            preparedHtml = contentHtml;
        }

        ReverseMarkdown.Converter converter = new(MarkdownConfig);
        string markdown;
        try
        {
            markdown = converter.Convert(preparedHtml);
        }
        catch
        {
            return string.Empty;
        }

        // Restore stashed complex tables as raw inline HTML.
        for (int i = 0; i < stashedTables.Count; i++)
            markdown = markdown.Replace(TablePlaceholderPrefix + i, "\n\n" + stashedTables[i] + "\n\n");

        return MultiBlankLine.Replace(markdown, "\n\n").Trim();
    }

    /// <summary>Computes the effective base URL, honoring a <c>&lt;base href&gt;</c> if present.</summary>
    private static Uri ResolveEffectiveBase(IHtmlDocument doc, Uri baseUrl)
    {
        string? baseHref = doc.QuerySelector("base[href]")?.GetAttribute("href");
        if (!string.IsNullOrWhiteSpace(baseHref) && Uri.TryCreate(baseUrl, baseHref, out Uri? combined))
            return combined;
        return baseUrl;
    }

    /// <summary>Rewrites relative <c>href</c>/<c>src</c> attributes to absolute URLs against the base.</summary>
    private static void ResolveUrls(IHtmlDocument doc, Uri baseUrl)
    {
        ResolveAttribute(doc, "a[href]", "href", baseUrl);
        ResolveAttribute(doc, "img[src]", "src", baseUrl);
        ResolveAttribute(doc, "source[src]", "src", baseUrl);
        ResolveAttribute(doc, "link[href]", "href", baseUrl);
    }

    /// <summary>Resolves a single attribute across all matching elements, skipping non-navigational schemes.</summary>
    private static void ResolveAttribute(IHtmlDocument doc, string selector, string attribute, Uri baseUrl)
    {
        foreach (IElement el in doc.QuerySelectorAll(selector))
        {
            string? raw = el.GetAttribute(attribute);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.StartsWith('#') ||
                raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Uri.TryCreate(baseUrl, raw, out Uri? abs))
                el.SetAttribute(attribute, abs.ToString());
        }
    }

    /// <summary>
    /// Replaces complex tables (rowspan/colspan or nested tables) with a text placeholder and records
    /// their raw HTML, so they survive Markdown conversion intact.
    /// </summary>
    private static void StashComplexTables(IHtmlDocument doc, List<string> stash)
    {
        foreach (IElement table in doc.QuerySelectorAll("table"))
        {
            bool isComplex = table.QuerySelector("[rowspan],[colspan]") != null
                             || table.QuerySelectorAll("table").Length > 0;
            if (!isComplex) continue;

            string token = TablePlaceholderPrefix + stash.Count;
            stash.Add(table.OuterHtml);

            IElement placeholder = doc.CreateElement("p");
            placeholder.TextContent = token;
            table.Replace(placeholder);
        }
    }
}
