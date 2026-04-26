// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ReviDotNet.Forge.Api;
using ReviDotNet.Forge.Services;
using Revi;

namespace ReviDotNet.Forge.Services.Gateway;

public class GatewayRouterService(
    PromptRegistryService prompts,
    IForgeRateLimiterService rateLimiter)
{
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private const int CooldownSeconds = 60;

    public async IAsyncEnumerable<string> RouteStreamAsync(
        ForgeInferRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidates(request);
        if (candidates.Count == 0)
        {
            yield return BuildSseEvent("error", """{"message":"No eligible models available"}""");
            yield break;
        }

        var messages = BuildMessages(request);

        foreach (var model in candidates)
        {
            if (IsInCooldown(model.Name)) continue;
            if (model.Provider?.InferenceClient is null) continue;

            // Acquire the stream handle outside any try/catch so we can yield inside try/finally
            StreamingResult<string>? streamHandle = null;
            try
            {
                await rateLimiter.AcquireAsync(model.Provider.Name!, cancellationToken);
                streamHandle = model.Provider.InferenceClient.GenerateStreamAsync(
                    messages: messages,
                    model: model.ModelString ?? "default",
                    temperature: request.Temperature,
                    maxTokens: request.MaxTokens,
                    inactivityTimeoutSeconds: request.InactivityTimeoutSeconds,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch
            {
                AddCooldown(model.Name);
                rateLimiter.Release(model.Provider.Name!);
                continue;
            }

            // Phase 2: stream chunks — try/finally only (no catch) so yield return is legal
            var success = false;
            var startTime = DateTime.UtcNow;
            try
            {
                await foreach (var chunk in streamHandle.Stream.WithCancellation(cancellationToken))
                    yield return BuildSseEvent("chunk", System.Text.Json.JsonSerializer.Serialize(new { text = chunk }));

                var meta = await streamHandle.Completion;
                if (meta.IsSuccess)
                {
                    success = true;
                    yield return BuildSseEvent("done", System.Text.Json.JsonSerializer.Serialize(new
                    {
                        model = model.Name,
                        provider = model.Provider.Name,
                        latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
                    }));
                }
            }
            finally
            {
                rateLimiter.Release(model.Provider.Name!);
                if (!success) AddCooldown(model.Name);
            }

            if (success) yield break;
        }

        yield return BuildSseEvent("error", """{"message":"All candidate models failed or are in cooldown"}""");
    }

    public async Task<(bool Success, string? Output, string ModelName, string ProviderName, string? Error)>
        RouteAsync(ForgeInferRequest request, CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidates(request);
        if (candidates.Count == 0)
            return (false, null, string.Empty, string.Empty, "No eligible models available");

        var messages = BuildMessages(request);

        foreach (var model in candidates)
        {
            if (IsInCooldown(model.Name)) continue;
            if (model.Provider?.InferenceClient is null) continue;

            await rateLimiter.AcquireAsync(model.Provider.Name!, cancellationToken);
            try
            {
                var result = await model.Provider.InferenceClient.GenerateAsync(
                    messages: messages,
                    model: model.ModelString ?? "default",
                    temperature: request.Temperature,
                    maxTokens: request.MaxTokens,
                    inactivityTimeoutSeconds: request.InactivityTimeoutSeconds,
                    cancellationToken: cancellationToken);

                return (true, result.Selected, model.Name, model.Provider.Name!, null);
            }
            catch (OperationCanceledException)
            {
                return (false, null, string.Empty, string.Empty, "Cancelled");
            }
            catch (Exception ex)
            {
                AddCooldown(model.Name);
                _ = ex;
            }
            finally
            {
                rateLimiter.Release(model.Provider?.Name ?? string.Empty);
            }
        }

        return (false, null, string.Empty, string.Empty, "All candidate models failed or are in cooldown");
    }

    private List<ModelProfile> GetCandidates(ForgeInferRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ExplicitModel))
        {
            var explicit_ = ModelManager.Get(request.ExplicitModel);
            return explicit_ is not null ? [explicit_] : [];
        }

        if (request.PreferredModels?.Count > 0)
        {
            return request.PreferredModels
                .Select(name => ModelManager.Get(name))
                .Where(m => m is not null && m.Enabled && !IsBlocked(m.Name, request.BlockedModels))
                .Select(m => m!)
                .ToList();
        }

        bool needsCompletion = request.CompletionType is Revi.CompletionType.PromptOnly
            or Revi.CompletionType.PromptChatOne
            or Revi.CompletionType.PromptChatMulti;

        return ModelManager.GetAll()
            .Where(m => m.Enabled
                && (request.MinTier is null || m.Tier >= request.MinTier)
                && !IsBlocked(m.Name, request.BlockedModels)
                && m.Provider?.InferenceClient is not null)
            .OrderBy(m => m.Tier)
            .ToList();
    }

    private List<Message> BuildMessages(ForgeInferRequest request)
    {
        var messages = new List<Message>();

        string? systemContent = null;
        if (!string.IsNullOrWhiteSpace(request.PromptName))
        {
            var prompt = prompts.GetByName(request.PromptName);
            systemContent = prompt?.System ?? prompt?.Instruction;
        }
        if (string.IsNullOrWhiteSpace(systemContent) && !string.IsNullOrWhiteSpace(request.PromptContent))
            systemContent = request.PromptContent;

        if (!string.IsNullOrWhiteSpace(systemContent))
            messages.Add(new Message("system", systemContent));

        if (request.Inputs?.Count > 0)
        {
            foreach (var input in request.Inputs)
            {
                var content = string.IsNullOrWhiteSpace(input.Label)
                    ? input.Text
                    : $"{input.Label}:\n{input.Text}";
                messages.Add(new Message("user", content));
            }
        }

        if (messages.Count == 0)
            messages.Add(new Message("user", string.Empty));

        return messages;
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

    private static string BuildSseEvent(string eventName, string data) =>
        $"event: {eventName}\ndata: {data}\n\n";
}
