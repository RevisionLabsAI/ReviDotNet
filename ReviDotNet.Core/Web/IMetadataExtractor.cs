// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>Extracts metadata via the JSON-LD → OG → Twitter → meta → heuristic ladder.</summary>
public interface IMetadataExtractor
{
    /// <summary>Returns structured metadata for the page.</summary>
    /// <param name="html">The raw page HTML.</param>
    /// <param name="baseUrl">The page URL, used to resolve relative canonical/image URLs.</param>
    WebMetadata Extract(string html, Uri baseUrl);
}
