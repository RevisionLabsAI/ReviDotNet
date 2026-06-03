// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// Built-in web extract tool. Fetches a URL and returns structured JSON: page metadata
/// (title/author/date/site/canonical/language/tags/description) plus heading-aware, token-bounded
/// content chunks. Useful when an agent needs structured provenance and chunked context rather than a
/// single Markdown blob.
///
/// Input is either a bare URL or a JSON object: <c>{ "url": "...", "maxTokens": 400 }</c>.
/// </summary>
public class WebExtractTool : IBuiltInTool
{
    private readonly IWebContentService _web;

    /// <summary>Creates the tool with a fully-default pipeline (used by the legacy static registry / non-DI callers).</summary>
    public WebExtractTool() : this(WebContentService.CreateDefault()) { }

    /// <summary>Creates the tool over an injected <see cref="IWebContentService"/> (the DI path).</summary>
    public WebExtractTool(IWebContentService web) => _web = web;

    /// <inheritdoc/>
    public string Name => "web-extract";

    /// <inheritdoc/>
    public string Description =>
        "Fetches a URL and returns structured JSON: metadata (title/author/date/site/canonical/language/tags) " +
        "plus heading-aware content chunks. Input is a URL, or JSON {\"url\":\"...\",\"maxTokens\":400}.";

    /// <inheritdoc/>
    public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        string trimmed = (input ?? string.Empty).Trim();
        string url = trimmed;
        int maxTokens = 400;

        if (trimmed.StartsWith('{'))
        {
            try
            {
                JObject req = JObject.Parse(trimmed);
                url = (req["url"] ?? req["uri"])?.ToString()?.Trim() ?? string.Empty;
                if (req["maxTokens"] is JToken mt && mt.Type == JTokenType.Integer)
                    maxTokens = Math.Clamp(mt.Value<int>(), 64, 2000);
            }
            catch (Exception ex)
            {
                return Fail($"Could not parse JSON input: {ex.Message}. Expected a URL or {{\"url\":\"...\"}}.");
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return Fail($"Invalid URL: '{url}'");

        try
        {
            WebFetchOptions options = new()
            {
                Chunk = true,
                ChunkOptions = new ChunkOptions { MaxTokens = maxTokens },
            };

            WebDocument doc = await _web.FetchAsync(url, options, token);

            if (string.IsNullOrWhiteSpace(doc.Markdown) && doc.FetchInfo.Blocked)
                return Fail($"Fetch blocked or challenged (status {doc.FetchInfo.StatusCode}) for {url}.");

            var payload = new
            {
                url = doc.Url,
                canonicalUrl = doc.CanonicalUrl,
                title = doc.Title,
                author = doc.Author,
                publishedAt = doc.PublishedAt,
                modifiedAt = doc.ModifiedAt,
                description = doc.Description,
                siteName = doc.SiteName,
                language = doc.Language,
                tags = doc.Tags,
                leadImageUrl = doc.LeadImageUrl,
                fetch = new { tier = doc.FetchInfo.Tier.ToString(), status = doc.FetchInfo.StatusCode, elapsedMs = doc.FetchInfo.ElapsedMs },
                chunkCount = doc.Chunks.Count,
                chunks = doc.Chunks.Select(c => new
                {
                    index = c.Index,
                    headingTrail = c.HeadingTrail,
                    estimatedTokens = c.EstimatedTokens,
                    text = c.Text,
                }),
            };

            return new ToolCallResult
            {
                ToolName = Name,
                Output = JsonConvert.SerializeObject(payload, Formatting.Indented),
            };
        }
        catch (Exception ex)
        {
            return Fail($"web-extract exception: {ex.Message}");
        }
    }

    /// <summary>Builds a failed <see cref="ToolCallResult"/> with the given message.</summary>
    private ToolCallResult Fail(string message)
        => new() { ToolName = Name, Failed = true, ErrorMessage = message };
}
