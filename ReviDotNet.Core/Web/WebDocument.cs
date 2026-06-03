// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;

namespace Revi;

/// <summary>An extracted, LLM-ready representation of one web page.</summary>
public sealed record WebDocument
{
    /// <summary>Final URL after redirects.</summary>
    public required string Url { get; init; }

    /// <summary>Canonical URL (rel=canonical / og:url) used for dedup; null if absent.</summary>
    public string? CanonicalUrl { get; init; }

    /// <summary>Extracted main-content title.</summary>
    public string? Title { get; init; }

    /// <summary>Author/byline if detected.</summary>
    public string? Author { get; init; }

    /// <summary>Publication timestamp if detected.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Last-modified timestamp if detected.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Meta/OG description.</summary>
    public string? Description { get; init; }

    /// <summary>BCP-47 language tag detected for the main content.</summary>
    public string? Language { get; init; }

    /// <summary>Publisher / site name.</summary>
    public string? SiteName { get; init; }

    /// <summary>Tags/keywords associated with the page.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Lead/featured image URL if detected (absolute).</summary>
    public string? LeadImageUrl { get; init; }

    /// <summary>Clean Markdown of the main content with boilerplate removed.</summary>
    public required string Markdown { get; init; }

    /// <summary>Optional heading- and token-aware chunks for embedding or agent context.</summary>
    public IReadOnlyList<WebChunk> Chunks { get; init; } = [];

    /// <summary>Diagnostics: fetch tier used, HTTP status, timing, fingerprint, block signals.</summary>
    public required WebFetchInfo FetchInfo { get; init; }

    /// <summary>
    /// Renders YAML frontmatter (title/author/dates/url/canonical/language/site/tags) followed by the
    /// Markdown body — the canonical LLM-ready form. Only non-empty fields are emitted.
    /// </summary>
    public string ToFrontmatterMarkdown()
    {
        StringBuilder sb = new();
        sb.Append("---\n");
        AppendYaml(sb, "title", Title);
        AppendYaml(sb, "author", Author);
        AppendYaml(sb, "published", PublishedAt?.ToString("o"));
        AppendYaml(sb, "modified", ModifiedAt?.ToString("o"));
        AppendYaml(sb, "url", Url);
        AppendYaml(sb, "canonical_url", CanonicalUrl);
        AppendYaml(sb, "site_name", SiteName);
        AppendYaml(sb, "language", Language);
        AppendYaml(sb, "description", Description);
        if (Tags.Count > 0)
            AppendYaml(sb, "tags", "[" + string.Join(", ", Tags.Select(EscapeYaml)) + "]", quote: false);
        sb.Append("---\n\n");
        sb.Append(Markdown);
        return sb.ToString();
    }

    /// <summary>Appends a single <c>key: value</c> line, skipping null/empty values.</summary>
    private static void AppendYaml(StringBuilder sb, string key, string? value, bool quote = true)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(key).Append(": ");
        sb.Append(quote ? EscapeYaml(value) : value);
        sb.Append('\n');
    }

    /// <summary>Wraps a scalar in double quotes and escapes embedded quotes/newlines for safe YAML.</summary>
    private static string EscapeYaml(string value)
    {
        string v = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Trim();
        return "\"" + v + "\"";
    }
}

/// <summary>A heading- and token-aware slice of a document's Markdown, suitable for embedding or context.</summary>
public sealed record WebChunk
{
    /// <summary>Zero-based position of this chunk within the document.</summary>
    public required int Index { get; init; }

    /// <summary>The breadcrumb of Markdown headings this chunk falls under (e.g. "Title &gt; Section").</summary>
    public string? HeadingTrail { get; init; }

    /// <summary>The chunk text (optionally prefixed with the heading trail).</summary>
    public required string Text { get; init; }

    /// <summary>Estimated token count for this chunk.</summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>Per-fetch diagnostics attached to a <see cref="WebDocument"/>.</summary>
public sealed record WebFetchInfo
{
    /// <summary>The fetch tier that ultimately served the page.</summary>
    public WebFetchTier Tier { get; init; }

    /// <summary>Final HTTP status code (0 if not applicable).</summary>
    public int StatusCode { get; init; }

    /// <summary>Wall-clock time spent fetching, in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Response content type, if known.</summary>
    public string? ContentType { get; init; }

    /// <summary>Length of the raw fetched body in characters.</summary>
    public int RawLength { get; init; }

    /// <summary>Whether the fetch was judged to be blocked/challenged.</summary>
    public bool Blocked { get; init; }

    /// <summary>Free-form diagnostic note (e.g. escalation reason, challenge marker).</summary>
    public string? Note { get; init; }
}
