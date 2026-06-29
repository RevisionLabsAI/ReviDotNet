// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using Revi;
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

        // ── Wave-2 optimize / test / calibration / scenario surfaces ─────────────────────────────────
        // These run live inference (optimize/test/generate). They resolve their services from DI as handler
        // params and honour the same optional ApiKey filter applied to the whole group above.

        // POST /optimize — run a prompt across models×runs, analyse each result, aggregate into suggestions,
        // then buffer the reviser stream into the full revised .pmt content.
        api.MapPost("/optimize", async (
            OptimizeRequest req,
            OptimizerService optimizer,
            IPromptManager prompts,
            IInferService infer,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.PromptName))
                return Results.BadRequest(new { error = "promptName is required" });
            if (req.ModelNames is not { Length: > 0 })
                return Results.BadRequest(new { error = "modelNames must contain at least one model" });

            Prompt? prompt = prompts.Get(req.PromptName);
            if (prompt is null)
                return Results.NotFound(new { error = $"prompt '{req.PromptName}' not found" });

            int runsPerModel = req.RunsPerModel is > 0 ? req.RunsPerModel.Value : 1;
            List<Input> inputs = (req.Inputs ?? [])
                .Select(i => new Input(i.Key, i.Value))
                .ToList();

            // Run each model the requested number of times and analyse every non-empty response.
            var analyses = new List<AnalysisResult>();
            foreach (string modelName in req.ModelNames)
            {
                if (string.IsNullOrWhiteSpace(modelName))
                    continue;

                for (int run = 0; run < runsPerModel; run++)
                {
                    ct.ThrowIfCancellationRequested();

                    CompletionResult? completion =
                        await infer.Completion(prompt, inputs, modelName: modelName, token: ct);
                    string? response = completion?.Selected;
                    if (string.IsNullOrWhiteSpace(response))
                        continue;

                    AnalysisResult? analysis =
                        await optimizer.AnalyzeAsync(req.PromptName, modelName, inputs, response);
                    if (analysis is not null)
                        analyses.Add(analysis);
                }
            }

            List<PromptSuggestion> suggestions =
                await optimizer.GenerateSuggestionsAsync(prompt, analyses);

            if (req.MaxSuggestions is > 0 && suggestions.Count > req.MaxSuggestions.Value)
                suggestions = suggestions.Take(req.MaxSuggestions.Value).ToList();

            // Buffer the reviser stream into the full revised prompt content.
            var revised = new StringBuilder();
            if (suggestions.Count > 0)
            {
                await foreach (string token in optimizer.ReviseStreamAsync(prompt, suggestions, ct))
                    revised.Append(token);
            }

            return Results.Ok(new OptimizeResponse(suggestions, revised.ToString()));
        });

        // POST /test/run — look up a saved suite by name and run it (agent- or prompt-mode), returning the
        // SuiteRunSummary. 404 when the named suite does not exist.
        api.MapPost("/test/run", async (
            TestRunRequest req,
            SavedSuitesService suites,
            AgentTestRunnerService runner,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.SuiteName))
                return Results.BadRequest(new { error = "suiteName is required" });

            SavedSuite? suite = suites.ListAll()
                .FirstOrDefault(s => string.Equals(s.Name, req.SuiteName, StringComparison.OrdinalIgnoreCase));
            if (suite is null)
                return Results.NotFound(new { error = $"suite '{req.SuiteName}' not found" });

            SuiteRunSummary summary = await runner.RunSuiteAsync(suite, req.AgentName, ct);
            return Results.Ok(summary);
        });

        // GET /calibration?agent=<name>&version=<opt> — confidence-vs-accuracy calibration for a fact-checker.
        api.MapGet("/calibration", async (
            string? agent,
            string? version,
            CalibrationAnalyzer analyzer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(agent))
                return Results.BadRequest(new { error = "agent query parameter is required" });

            CalibrationReport report = await analyzer.AnalyzeAsync(agent, version, ct);
            return Results.Ok(report);
        });

        // POST /generate-scenarios — author fresh evaluation scenarios for an agent in a target category.
        api.MapPost("/generate-scenarios", async (
            GenerateScenariosRequest req,
            ScenarioGenerator generator,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.AgentName))
                return Results.BadRequest(new { error = "agentName is required" });
            if (string.IsNullOrWhiteSpace(req.AgentSpecSection))
                return Results.BadRequest(new { error = "agentSpecSection is required" });
            if (string.IsNullOrWhiteSpace(req.TargetCategory))
                return Results.BadRequest(new { error = "targetCategory is required" });

            int count = req.Count is > 0 ? req.Count.Value : 5;
            IReadOnlyList<Scenario> scenarios = await generator.GenerateAsync(
                req.AgentName,
                req.AgentSpecSection,
                existing: [],
                req.TargetCategory,
                count,
                ct);

            return Results.Ok(scenarios);
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
