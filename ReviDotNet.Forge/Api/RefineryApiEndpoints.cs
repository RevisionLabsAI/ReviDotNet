// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi.Refinery;
using Revi.Refinery.Hosting;

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

        api.MapGet("/campaigns/{id}", async (string id, ICampaignStore store, CancellationToken ct) =>
        {
            Campaign? c = await store.GetAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
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
