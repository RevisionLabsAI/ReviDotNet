// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>Converts cleaned content HTML to LLM-friendly Markdown.</summary>
public interface IMarkdownConverter
{
    /// <summary>Walks the DOM to Markdown; resolves relative URLs; keeps complex tables as HTML.</summary>
    /// <param name="contentHtml">The cleaned main-content HTML.</param>
    /// <param name="baseUrl">The page URL, used to resolve relative <c>href</c>/<c>src</c> attributes.</param>
    string ToMarkdown(string contentHtml, Uri baseUrl);
}
