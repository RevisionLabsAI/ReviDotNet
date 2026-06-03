// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Built-in web scrape tool. Fetches a URL and returns its main content as clean, metadata-tagged
/// Markdown — boilerplate (nav/ads/footers) removed — via <see cref="IWebContentService"/>. This
/// replaces the old regex tag-strip, giving agents readable, structured content instead of soup.
///
/// The input is a URL.
/// </summary>
public class WebScrapeTool : IBuiltInTool
{
    private readonly IWebContentService _web;

    /// <summary>Creates the tool with a fully-default pipeline (used by the legacy static registry / non-DI callers).</summary>
    public WebScrapeTool() : this(WebContentService.CreateDefault()) { }

    /// <summary>Creates the tool over an injected <see cref="IWebContentService"/> (the DI path).</summary>
    public WebScrapeTool(IWebContentService web) => _web = web;

    /// <inheritdoc/>
    public string Name => "web-scrape";

    /// <inheritdoc/>
    public string Description => "Fetches a URL and returns its main content as clean, metadata-tagged Markdown (boilerplate removed). Input is a URL.";

    /// <summary>Hard cap on returned characters so a huge page cannot blow the agent's context window.</summary>
    private const int MaxChars = 50_000;

    /// <inheritdoc/>
    public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        string url = (input ?? string.Empty).Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = $"Invalid URL: '{url}'"
            };
        }

        try
        {
            WebDocument doc = await _web.FetchAsync(url, options: null, cancellationToken: token);

            if (string.IsNullOrWhiteSpace(doc.Markdown) && doc.FetchInfo.Blocked)
            {
                return new ToolCallResult
                {
                    ToolName = Name,
                    Failed = true,
                    ErrorMessage = $"Fetch blocked or challenged (status {doc.FetchInfo.StatusCode}) for {url}. " +
                                   "A browser-tier fetcher (ReviDotNet.Scraping) may be required."
                };
            }

            string output = doc.ToFrontmatterMarkdown();
            if (output.Length > MaxChars)
                output = output[..MaxChars] + "\n\n[...truncated]";

            return new ToolCallResult
            {
                ToolName = Name,
                Output = output.Trim()
            };
        }
        catch (Exception ex)
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = $"web-scrape exception: {ex.Message}"
            };
        }
    }
}
