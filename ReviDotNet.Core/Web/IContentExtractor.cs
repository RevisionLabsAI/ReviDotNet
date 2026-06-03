// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>Isolates the main content of a page from boilerplate (nav/ads/footer).</summary>
public interface IContentExtractor
{
    /// <summary>Returns the cleaned content HTML plus structured metadata.</summary>
    /// <param name="html">The raw page HTML.</param>
    /// <param name="baseUrl">The page URL, used to resolve relative links and as extraction context.</param>
    ExtractedContent Extract(string html, Uri baseUrl);
}
