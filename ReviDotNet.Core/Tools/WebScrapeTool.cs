// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net.Http;
using System.Text.RegularExpressions;

namespace Revi;

/// <summary>
/// Built-in web scrape tool. Fetches a URL and strips HTML to return readable plain text.
///
/// The input should be a URL. The tool performs an HTTP GET and converts the HTML body
/// to plain text by removing tags and decoding entities.
/// </summary>
public class WebScrapeTool : IBuiltInTool
{
    public string Name => "web-scrape";
    public string Description => "Fetches the content of a URL and returns it as plain text.";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "ReviDotNet/1.0 (agent-scraper)" } }
    };

    // Strips all HTML tags
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    // Collapses whitespace
    private static readonly Regex WhitespaceRegex = new(@"\s{3,}", RegexOptions.Compiled);

    public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        string url = input.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
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
            using var response = await _http.GetAsync(uri, token);
            string body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new ToolCallResult
                {
                    ToolName = Name,
                    Failed = true,
                    ErrorMessage = $"HTTP {(int)response.StatusCode} from {url}"
                };
            }

            // Strip HTML tags and normalize whitespace
            string text = TagRegex.Replace(body, " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = WhitespaceRegex.Replace(text, "\n\n");

            // Truncate to a reasonable size
            const int maxChars = 20_000;
            if (text.Length > maxChars)
                text = text[..maxChars] + "\n[...truncated]";

            return new ToolCallResult
            {
                ToolName = Name,
                Output = text.Trim()
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
