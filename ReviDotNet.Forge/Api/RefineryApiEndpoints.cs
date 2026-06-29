// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi.Refinery;
using Revi.Refinery.Hosting;
using ReviDotNet.Forge.Services;

namespace ReviDotNet.Forge.Api;

/// <summary>
/// HTTP control API for the Refinery (plugins + campaigns). The same backend the <c>/refinery</c> dashboard
/// uses, exposed for the <c>revi</c> CLI and other clients. Campaign execution endpoints land with the first
/// real plugin (Phase 3).
/// </summary>
public static class RefineryApiEndpoints
{
    public static void MapRefineryApi(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/refinery");

        api.MapGet("/plugins", (PluginManager plugins) =>
            Results.Ok(plugins.Catalog.Select(ToDto).ToList()));

        api.MapPost("/plugins/refresh", async (PluginManager plugins, CancellationToken ct) =>
        {
            await plugins.RefreshAllAsync(ct);
            return Results.Ok(plugins.Catalog.Select(ToDto).ToList());
        });

        api.MapPost("/plugins/{name}/reload", async (string name, PluginManager plugins, CancellationToken ct) =>
        {
            await plugins.ReloadAsync(name, ct);
            LoadedPlugin? lp = plugins.Get(name);
            return lp is null
                ? Results.NotFound(new { error = $"plugin '{name}' not found" })
                : Results.Ok(ToDto(lp));
        });

        api.MapGet("/campaigns", async (ICampaignStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        api.MapPost("/campaigns", async (CampaignSpec spec, RefineryCampaignService campaigns, CancellationToken ct) =>
        {
            try
            {
                // AutoPropose => full refinement campaign; otherwise just measure a baseline.
                string id = spec.AutoPropose
                    ? await campaigns.StartCampaignAsync(spec, ct)
                    : await campaigns.StartBaselineAsync(spec, ct);
                return Results.Accepted($"/api/refinery/campaigns/{id}", new { id, status = "Pending" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/campaigns/{id}", async (string id, ICampaignStore store, CancellationToken ct) =>
        {
            Campaign? c = await store.GetAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });

        api.MapGet("/campaigns/{id}/ledger", async (string id, ICampaignStore store, CancellationToken ct) =>
        {
            IReadOnlyList<LedgerEntry> entries = await store.GetLedgerAsync(id, ct);
            return Results.Ok(entries);
        });

        api.MapPost("/campaigns/{id}/promote/{variantId}",
            async (string id, string variantId, RefineryCampaignService campaigns, CancellationToken ct) =>
        {
            bool promoted = await campaigns.PromoteVariantAsync(id, variantId, ct);
            return promoted
                ? Results.Ok(new { promoted = true })
                : Results.BadRequest(new { promoted = false, error = "variant could not be promoted (see server log)" });
        });
    }

    private static object ToDto(LoadedPlugin p) => new
    {
        name = p.Name,
        status = p.Status.ToString(),
        repoPath = p.RepoPath,
        projectPath = p.ProjectPath,
        error = p.Error,
        warning = p.Warning,
        loadedAt = p.LoadedAt,
        agents = p.Plugin?.GetAgents().Select(a => new { a.Name, a.Description }).ToList(),
        suites = p.Plugin?.GetScenarioSuites()
            .Select(s => new { s.Name, s.AgentName, scenarios = s.Scenarios.Count }).ToList(),
        invariants = p.Plugin?.GetInvariantCheckers()
            .Select(i => new { i.Id, i.Description, severity = i.Severity.ToString() }).ToList()
    };
}
