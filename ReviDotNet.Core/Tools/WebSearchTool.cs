// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net.Http;
using System.Web;

namespace Revi;

/// <summary>
/// Built-in web search tool. Submits a query to a configurable search API and returns results.
///
/// Configuration via environment variables:
///   REVI_SEARCH_URL  — Base URL of the search API (e.g. https://api.search.brave.com/res/v1/web/search)
///   REVI_SEARCH_KEY  — API key for the search service
/// </summary>
public class WebSearchTool : IBuiltInTool
{
    public string Name => "web-search";
    public string Description => "Searches the web and returns a list of relevant results for the given query.";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        string? searchUrl = Environment.GetEnvironmentVariable("REVI_SEARCH_URL");
        string? searchKey = Environment.GetEnvironmentVariable("REVI_SEARCH_KEY");

        if (string.IsNullOrWhiteSpace(searchUrl))
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = "REVI_SEARCH_URL environment variable is not set. Cannot execute web-search."
            };
        }

        try
        {
            string encodedQuery = HttpUtility.UrlEncode(input.Trim());
            string requestUrl = searchUrl.Contains('?')
                ? $"{searchUrl}&q={encodedQuery}"
                : $"{searchUrl}?q={encodedQuery}";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (!string.IsNullOrWhiteSpace(searchKey))
                request.Headers.Add("X-Subscription-Token", searchKey);

            using var response = await _http.SendAsync(request, token);
            string body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new ToolCallResult
                {
                    ToolName = Name,
                    Failed = true,
                    ErrorMessage = $"Search API returned {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}"
                };
            }

            return new ToolCallResult
            {
                ToolName = Name,
                Output = body
            };
        }
        catch (Exception ex)
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = $"web-search exception: {ex.Message}"
            };
        }
    }
}
