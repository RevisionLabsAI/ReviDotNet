// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Revi.Refinery;

namespace ReviDotNet.Cli;

/// <summary>
/// Thin typed wrapper around the Forge Refinery Control API.
/// All methods throw <see cref="RefineryHttpException"/> on non-success HTTP responses
/// or <see cref="HttpRequestException"/> on connection failures.
/// </summary>
internal sealed class RefineryClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // The server (ASP.NET default) serializes enums as numbers; if it ever switches to string
        // enums, this converter accepts BOTH representations on read, so CampaignStatus stays parseable.
        Converters = { new JsonStringEnumConverter() },
    };

    public RefineryClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>The resolved Forge base address this client targets.</summary>
    public Uri BaseAddress => _http.BaseAddress!;

    // ------------------------------------------------------------------ plugins

    public async Task<JsonDocument> ListPluginsRawAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync("api/refinery/plugins", ct);
        await EnsureSuccessAsync(resp);
        string json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument> RefreshPluginsRawAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.PostAsync("api/refinery/plugins/refresh", null, ct);
        await EnsureSuccessAsync(resp);
        string json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument> ReloadPluginRawAsync(string name, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.PostAsync($"api/refinery/plugins/{Uri.EscapeDataString(name)}/reload", null, ct);
        await EnsureSuccessAsync(resp);
        string json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    // ------------------------------------------------------------------ campaigns

    /// <summary>POST /api/refinery/campaigns — returns the {id, status} starter DTO.</summary>
    public async Task<(string Id, string Status)> StartCampaignAsync(CampaignSpec spec, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.PostAsJsonAsync("api/refinery/campaigns", spec, JsonOpts, ct);
        await EnsureSuccessAsync(resp);
        using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        string id = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Server returned no campaign id.");
        string status = doc.RootElement.TryGetProperty("status", out JsonElement se) ? (se.GetString() ?? "Pending") : "Pending";
        return (id, status);
    }

    public async Task<Campaign> GetCampaignAsync(string id, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync($"api/refinery/campaigns/{Uri.EscapeDataString(id)}", ct);
        await EnsureSuccessAsync(resp);
        Campaign? c = await resp.Content.ReadFromJsonAsync<Campaign>(JsonOpts, ct);
        return c ?? throw new InvalidOperationException("Server returned null campaign.");
    }

    public async Task<Campaign[]> ListCampaignsAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync("api/refinery/campaigns", ct);
        await EnsureSuccessAsync(resp);
        Campaign[]? list = await resp.Content.ReadFromJsonAsync<Campaign[]>(JsonOpts, ct);
        return list ?? [];
    }

    public async Task<JsonDocument> GetCampaignRawAsync(string id, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync($"api/refinery/campaigns/{Uri.EscapeDataString(id)}", ct);
        await EnsureSuccessAsync(resp);
        string json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument> ListCampaignsRawAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync("api/refinery/campaigns", ct);
        await EnsureSuccessAsync(resp);
        string json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    // ------------------------------------------------------------------ ledger

    public async Task<LedgerEntry[]> GetLedgerAsync(string campaignId, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync($"api/refinery/campaigns/{Uri.EscapeDataString(campaignId)}/ledger", ct);
        await EnsureSuccessAsync(resp);
        LedgerEntry[]? list = await resp.Content.ReadFromJsonAsync<LedgerEntry[]>(JsonOpts, ct);
        return list ?? [];
    }

    public async Task<JsonDocument> GetLedgerRawAsync(string campaignId, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.GetAsync($"api/refinery/campaigns/{Uri.EscapeDataString(campaignId)}/ledger", ct);
        await EnsureSuccessAsync(resp);
        string json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;

        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(); } catch { /* ignore */ }

        // Try to extract { error } from JSON body.
        string? errorMsg = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out JsonElement e))
                    errorMsg = e.GetString();
            }
            catch { /* not JSON */ }
        }

        throw new RefineryHttpException((int)resp.StatusCode, errorMsg ?? body, resp.RequestMessage?.RequestUri?.ToString() ?? "");
    }
}

internal sealed class RefineryHttpException(int statusCode, string message, string url)
    : Exception($"HTTP {statusCode} from {url}: {message}")
{
    public int StatusCode { get; } = statusCode;
    public string Url { get; } = url;
}
