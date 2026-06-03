// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Structured page metadata gathered via the JSON-LD → OpenGraph → Twitter Card → standard meta →
/// DOM-heuristic precedence ladder. Emitted as YAML frontmatter so an agent gets provenance for free.
/// </summary>
public sealed record WebMetadata
{
    /// <summary>Document title.</summary>
    public string? Title { get; init; }

    /// <summary>Author/byline if detected.</summary>
    public string? Author { get; init; }

    /// <summary>Publication timestamp if detected.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Last-modified timestamp if detected.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Meta/OG description.</summary>
    public string? Description { get; init; }

    /// <summary>Canonical URL (rel=canonical / og:url) — the crawl dedup key; null if absent.</summary>
    public string? CanonicalUrl { get; init; }

    /// <summary>BCP-47 language tag detected for the main content.</summary>
    public string? Language { get; init; }

    /// <summary>Publisher / site name.</summary>
    public string? SiteName { get; init; }

    /// <summary>Tags/keywords associated with the page.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Lead/featured image URL if detected (absolute).</summary>
    public string? LeadImageUrl { get; init; }
}
