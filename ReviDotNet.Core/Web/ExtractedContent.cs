// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Result of isolating the main content of a page from boilerplate. Carries the cleaned content HTML
/// plus the metadata the extractor happened to recover (used as a fallback under the structured
/// metadata ladder).
/// </summary>
public sealed record ExtractedContent
{
    /// <summary>The cleaned main-content HTML with navigation/ads/footers removed.</summary>
    public required string ContentHtml { get; init; }

    /// <summary>Extracted main-content title.</summary>
    public string? Title { get; init; }

    /// <summary>Author/byline detected by the extractor.</summary>
    public string? Author { get; init; }

    /// <summary>Short excerpt/summary.</summary>
    public string? Excerpt { get; init; }

    /// <summary>Publisher / site name.</summary>
    public string? SiteName { get; init; }

    /// <summary>Detected content language (BCP-47).</summary>
    public string? Language { get; init; }

    /// <summary>Publication timestamp if detected.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Lead/featured image URL if detected (absolute).</summary>
    public string? LeadImageUrl { get; init; }

    /// <summary>Estimated reading time in minutes.</summary>
    public int? TimeToReadMinutes { get; init; }

    /// <summary>Length of the extracted plain text (used as the "short extract → escalate" signal).</summary>
    public int TextLength { get; init; }

    /// <summary>Whether the extractor judged the page to have meaningful readable content.</summary>
    public bool IsReadable { get; init; }
}
