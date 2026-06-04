// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using ReviDotNet.Forge.Api;
using ReviDotNet.Forge.Models;
using Revi;

namespace ReviDotNet.Forge.Services.Gateway;

/// <summary>
/// The embedding counterpart to <see cref="GatewayRouterService"/>. Selects an
/// eligible embedding model, fails over across candidates on error (with a short
/// per-model cooldown), enforces per-provider rate limits, and records every
/// attempt to the usage dashboard tagged <see cref="UsageType.Embedding"/>.
/// </summary>
public class EmbeddingGatewayRouterService(
    IForgeRateLimiterService rateLimiter,
    UsageDashboardService usageDashboard,
    IEmbeddingManager embeddings)
{
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private const int CooldownSeconds = 60;

    public async Task<(bool Success, List<float[]>? Embeddings, string ModelName, string ProviderName, string? Error, int InputTokens, int Dimensions)>
        RouteAsync(ForgeEmbedRequest request, CancellationToken cancellationToken = default, string clientApiKeyPrefix = "")
    {
        string[] inputs = ResolveInputs(request);
        if (inputs.Length == 0)
            return (false, null, string.Empty, string.Empty, "At least one input text is required", 0, 0);

        var candidates = GetCandidates(request);
        if (candidates.Count == 0)
            return (false, null, string.Empty, string.Empty, "No eligible embedding models available", 0, 0);

        int failoverAttempts = 0;

        foreach (var model in candidates)
        {
            if (IsInCooldown(model.Name)) continue;
            if (model.Provider?.EmbeddingClient is null) continue;

            DateTime startTime = DateTime.UtcNow;
            await rateLimiter.AcquireAsync(model.Provider.Name!, cancellationToken);
            try
            {
                var response = await model.Provider.EmbeddingClient.GenerateEmbeddingsAsync(
                    inputs: inputs,
                    model: string.IsNullOrWhiteSpace(model.ModelString) ? "default" : model.ModelString,
                    dimensions: request.Dimensions ?? model.Dimensions,
                    encodingFormat: request.EncodingFormat ?? model.EncodingFormat,
                    cancellationToken: cancellationToken);

                long latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

                // Treat an empty payload as a failure so we fail over to the next candidate.
                if (response?.Data is null || response.Data.Count == 0)
                {
                    AddCooldown(model.Name);
                    failoverAttempts++;
                    continue;
                }

                var vectors = response.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
                int inputTokens = response.Usage is not null && response.Usage.TryGetValue("prompt_tokens", out var pt)
                    ? pt
                    : EstimateInputTokens(inputs);
                int dimensions = vectors.Count > 0 ? vectors[0].Length : 0;

                _ = usageDashboard.RecordAsync(new ForgeUsageRecord
                {
                    ClientId = request.ClientId,
                    ClientApiKeyPrefix = clientApiKeyPrefix,
                    Timestamp = startTime,
                    ModelName = model.Name,
                    ProviderName = model.Provider.Name ?? string.Empty,
                    Success = true,
                    FailoverAttempts = failoverAttempts,
                    InputTokens = inputTokens,
                    OutputTokens = 0,
                    LatencyMs = latencyMs,
                    TtftMs = 0,
                    WasStreaming = false,
                    Type = UsageType.Embedding
                });

                return (true, vectors, model.Name, model.Provider.Name!, null, inputTokens, dimensions);
            }
            catch (OperationCanceledException)
            {
                _ = usageDashboard.RecordAsync(new ForgeUsageRecord
                {
                    ClientId = request.ClientId,
                    ClientApiKeyPrefix = clientApiKeyPrefix,
                    Timestamp = startTime,
                    ModelName = model.Name,
                    ProviderName = model.Provider?.Name ?? string.Empty,
                    Success = false,
                    FailureReason = "Cancelled",
                    FailoverAttempts = failoverAttempts,
                    WasStreaming = false,
                    Type = UsageType.Embedding
                });
                return (false, null, string.Empty, string.Empty, "Cancelled", 0, 0);
            }
            catch (Exception)
            {
                AddCooldown(model.Name);
                failoverAttempts++;
            }
            finally
            {
                rateLimiter.Release(model.Provider?.Name ?? string.Empty);
            }
        }

        _ = usageDashboard.RecordAsync(new ForgeUsageRecord
        {
            ClientId = request.ClientId,
            ClientApiKeyPrefix = clientApiKeyPrefix,
            Timestamp = DateTime.UtcNow,
            Success = false,
            FailureReason = "All candidate embedding models failed or are in cooldown",
            FailoverAttempts = failoverAttempts,
            WasStreaming = false,
            Type = UsageType.Embedding
        });

        return (false, null, string.Empty, string.Empty, "All candidate embedding models failed or are in cooldown", 0, 0);
    }

    private static string[] ResolveInputs(ForgeEmbedRequest request)
    {
        if (request.Inputs is { Count: > 0 })
            return request.Inputs.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (!string.IsNullOrWhiteSpace(request.Input))
            return [request.Input];
        return [];
    }

    private List<EmbeddingProfile> GetCandidates(ForgeEmbedRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ExplicitModel))
        {
            var explicit_ = embeddings.Get(request.ExplicitModel);
            return explicit_ is not null && explicit_.Enabled ? [explicit_] : [];
        }

        if (request.PreferredModels?.Count > 0)
        {
            return request.PreferredModels
                .Select(name => embeddings.Get(name))
                .Where(m => m is not null && m.Enabled && !IsBlocked(m.Name, request.BlockedModels))
                .Select(m => m!)
                .ToList();
        }

        return embeddings.GetAllEnabled()
            .Where(m => (request.MinTier is null || m.Tier >= request.MinTier)
                && !IsBlocked(m.Name, request.BlockedModels)
                && m.Provider?.EmbeddingClient is not null)
            .OrderBy(m => m.Tier)
            .ToList();
    }

    private bool IsInCooldown(string modelName)
    {
        if (_cooldowns.TryGetValue(modelName, out var until))
        {
            if (DateTime.UtcNow < until) return true;
            _cooldowns.TryRemove(modelName, out _);
        }
        return false;
    }

    private void AddCooldown(string modelName) =>
        _cooldowns[modelName] = DateTime.UtcNow.AddSeconds(CooldownSeconds);

    private static bool IsBlocked(string modelName, List<string>? blocked) =>
        blocked?.Contains(modelName, StringComparer.OrdinalIgnoreCase) == true;

    private static int EstimateInputTokens(string[] inputs) =>
        Util.EstTokenCountFromCharCount(inputs.Sum(i => i.Length));
}
