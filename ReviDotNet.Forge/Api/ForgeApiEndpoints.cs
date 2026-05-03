// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using Revi;
using ReviDotNet.Forge.Models;
using ReviDotNet.Forge.Services;
using ReviDotNet.Forge.Services.ApiKeys;
using ReviDotNet.Forge.Services.Gateway;

namespace ReviDotNet.Forge.Api;

public static class ForgeApiEndpoints
{
    public static void MapForgeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

        api.MapPost("/infer", HandleInferAsync);
        api.MapPost("/usage/report", HandleUsageReportAsync);
        api.MapGet("/prompts", HandleListPromptsAsync);
        api.MapGet("/prompts/{name}", HandleGetPromptAsync);
        api.MapGet("/models", HandleListModelsAsync);
    }

    private static async Task HandleInferAsync(
        HttpContext context,
        IForgeApiKeyService keyService,
        GatewayRouterService router)
    {
        if (!await ApiKeyAuth.ValidateAsync(context, keyService)) return;

        string apiKeyPrefix = GetApiKeyPrefix(context);

        ForgeInferRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ForgeInferRequest>();
        }
        catch
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid request body" });
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ClientId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "ClientId is required" });
            return;
        }

        if (request.Stream)
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            await foreach (var chunk in router.RouteStreamAsync(request, context.RequestAborted, apiKeyPrefix))
            {
                await context.Response.WriteAsync(chunk, Encoding.UTF8, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        else
        {
            var (success, output, modelName, providerName, error, inputTokens, outputTokens) =
                await router.RouteAsync(request, context.RequestAborted, apiKeyPrefix);

            var response = new ForgeInferResponse
            {
                Success = success,
                Output = output,
                ModelUsed = modelName,
                ProviderUsed = providerName,
                InputTokens = inputTokens > 0 ? inputTokens : null,
                OutputTokens = outputTokens > 0 ? outputTokens : null,
                ErrorMessage = error
            };

            if (!success) context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(response);
        }
    }

    private static async Task HandleUsageReportAsync(
        HttpContext context,
        IForgeApiKeyService keyService,
        UsageDashboardService usageDashboard)
    {
        if (!await ApiKeyAuth.ValidateAsync(context, keyService)) return;

        ForgeUsageReportRequest? report;
        try
        {
            report = await context.Request.ReadFromJsonAsync<ForgeUsageReportRequest>();
        }
        catch
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid request body" });
            return;
        }

        if (report is null || string.IsNullOrWhiteSpace(report.ClientId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "ClientId is required" });
            return;
        }

        await usageDashboard.RecordAsync(new ForgeUsageRecord
        {
            ClientId = report.ClientId,
            ClientApiKeyPrefix = GetApiKeyPrefix(context),
            Timestamp = DateTime.UtcNow,
            PromptName = report.PromptName ?? string.Empty,
            ModelName = report.ModelName,
            ProviderName = report.ProviderName,
            Success = report.Success,
            FailureReason = report.FailureReason,
            FailoverAttempts = 0,
            InputTokens = report.InputTokens,
            OutputTokens = report.OutputTokens,
            LatencyMs = report.LatencyMs,
            TtftMs = 0,
            WasStreaming = report.WasStreaming
        });

        await context.Response.WriteAsJsonAsync(new { recorded = true });
    }

    private static string GetApiKeyPrefix(HttpContext context)
    {
        string raw = context.Request.Headers["X-Forge-ApiKey"].ToString();
        return raw.Length > 8 ? raw[..8] + "..." : raw;
    }

    private static async Task HandleListPromptsAsync(
        HttpContext context,
        IForgeApiKeyService keyService,
        PromptRegistryService prompts)
    {
        if (!await ApiKeyAuth.ValidateAsync(context, keyService)) return;

        var names = prompts.GetAll().Select(p => p.Name).OrderBy(n => n).ToList();
        await context.Response.WriteAsJsonAsync(names);
    }

    private static async Task HandleGetPromptAsync(
        HttpContext context,
        IForgeApiKeyService keyService,
        PromptRegistryService prompts,
        string name)
    {
        if (!await ApiKeyAuth.ValidateAsync(context, keyService)) return;

        var prompt = prompts.GetByName(name);
        if (prompt is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error = $"Prompt '{name}' not found" });
            return;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            name = prompt.Name,
            system = prompt.System,
            instruction = prompt.Instruction
        });
    }

    private static async Task HandleListModelsAsync(
        HttpContext context,
        IForgeApiKeyService keyService,
        IModelManager modelManager)
    {
        if (!await ApiKeyAuth.ValidateAsync(context, keyService)) return;

        var models = modelManager.GetAll()
            .Where(m => m.Enabled)
            .Select(m => new
            {
                name = m.Name,
                tier = m.Tier.ToString(),
                provider = m.Provider?.Name,
                modelString = m.ModelString
            })
            .OrderBy(m => m.tier).ThenBy(m => m.name)
            .ToList();

        await context.Response.WriteAsJsonAsync(models);
    }
}
