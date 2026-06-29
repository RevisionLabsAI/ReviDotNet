// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi.Refinery;
using Revi.Refinery.Hosting;
using ReviDotNet.Forge.Services;
using ReviDotNet.Forge.Services.ApiKeys;

namespace ReviDotNet.Forge.Api;

/// <summary>
/// HTTP control API for the Refinery (plugins + campaigns). The same backend the <c>/refinery</c> dashboard
/// uses, exposed for the <c>revi</c> CLI and other clients. Campaign execution endpoints land with the first
/// real plugin (Phase 3).
/// </summary>
public static class RefineryApiEndpoints
{
    public static void MapRefineryApi(this WebApplication app, bool requireApiKey = false)
    {
        RouteGroupBuilder api = app.MapGroup("/api/refinery");

        // Config-gated API-key auth: when on, guard the whole group with the same ApiKeyAuth.ValidateAsync
        // check the /api/v1 gateway uses (Unauthorized + JSON error if it fails). Off by default so the local
        // CLI / dashboard keep working without a key.
        if (requireApiKey)
        {
            api.AddEndpointFilter(async (ctx, next) =>
            {
                IForgeApiKeyService keyService =
                    ctx.HttpContext.RequestServices.GetRequiredService<IForgeApiKeyService>();
                if (!await ApiKeyAuth.ValidateAsync(ctx.HttpContext, keyService))
                    return Results.Empty; // ValidateAsync already wrote the 401 response.
                return await next(ctx);
            });
        }

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

        // Knob-effectiveness rollup mined from the ledger across campaigns (optionally scoped to ?agent=).
        // MetaAnalyzer is registered in DI by the meta-analysis component; resolve it as a handler param.
        api.MapGet("/meta", async (MetaAnalyzer analyzer, string? agent, CancellationToken ct) =>
            Results.Ok(await analyzer.AnalyzeAsync(agent, ct)));
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
