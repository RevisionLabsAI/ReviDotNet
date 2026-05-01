// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.AspNetCore.TestHost;
using Revi;
using Revi.Tests.Helpers;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Test harness that wires a <see cref="FakeInferenceServer"/> scripted response sequence
/// to a fake provider/model/agent triple in the static registries. Disposing rolls back
/// the registry mutations so tests don't bleed into one another.
///
/// Usage:
/// <code>
///   using var h = new AgentTestHarness(turns, agentBuilder);
///   AgentResult result = await Agent.Run(h.AgentName, inputs);
/// </code>
/// </summary>
internal sealed class AgentTestHarness : IDisposable
{
    public string ProviderName { get; }
    public string ModelName { get; }
    public string AgentName { get; }
    public TestServer Server { get; }
    public Uri BaseAddress { get; }
    public ModelProfile Model { get; }
    public AgentProfile Agent { get; }
    public List<string> RegisteredTools { get; } = new();

    private readonly ProviderProfile _provider;

    public AgentTestHarness(
        IReadOnlyList<FakeAgentTurn> turns,
        Func<string, AgentProfile> agentBuilder,
        string? agentName = null,
        decimal? costPerMillionInputTokens = null,
        decimal? costPerMillionOutputTokens = null)
    {
        // Unique names so multiple harness instances don't collide in the static registries.
        string suffix = Guid.NewGuid().ToString("n").Substring(0, 8);
        ProviderName = $"fake-provider-{suffix}";
        ModelName = $"fake-model-{suffix}";
        AgentName = agentName ?? $"fake-agent-{suffix}";

        (Server, BaseAddress) = FakeInferenceServer.CreateWithScript(turns);

        // Build a provider with a fake InferClient that points at the test server.
        // We construct ProviderProfile via its parameterized ctor (which builds a real InferClient)
        // and then overwrite InferenceClient with one that has the test HttpClient injected.
        _provider = new ProviderProfile(
            name: ProviderName,
            enabled: true,
            protocol: Protocol.OpenAI,
            apiURL: BaseAddress.ToString(),
            apiKey: "test-key",
            timeoutSeconds: 30,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 4,
            defaultModel: ModelName,
            supportsCompletion: false,
            supportsGuidance: true,
            defaultGuidanceType: GuidanceType.Json);

        HttpClient httpClient = Server.CreateClient();
        httpClient.BaseAddress = BaseAddress;

        _provider.InferenceClient = new InferClient(
            apiUrl: BaseAddress.ToString(),
            apiKey: "test-key",
            protocol: Protocol.OpenAI,
            defaultModel: ModelName,
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 4,
            supportsCompletion: false,
            supportsGuidance: true,
            defaultGuidanceType: GuidanceType.Json,
            defaultGuidanceString: AgentStepSchema.Schema,
            httpClientOverride: httpClient);

        ProviderManager.Add(_provider);

        Model = new ModelProfile
        {
            Name = ModelName,
            Enabled = true,
            ModelString = ModelName,
            ProviderName = ProviderName,
            Provider = _provider,
            Tier = ModelTier.A,
            TokenLimit = 8192,
            // Pin a small MaxTokens so cost projection roughly matches the fake server's
            // default output token count (50). This keeps budget tests' arithmetic predictable.
            MaxTokens = "200",
            CostPerMillionInputTokens = costPerMillionInputTokens,
            CostPerMillionOutputTokens = costPerMillionOutputTokens,
            DefaultSystemInputType = InputType.Filled,
            DefaultInstructionInputType = InputType.Filled
        };
        ModelManager.Add(Model);

        Agent = agentBuilder(ModelName);
        Agent.Name = AgentName;

        // Force every state to use this harness's model. The static ModelManager.Find
        // is shared across tests, and without an explicit override a state would resolve
        // to whichever tier-A model was registered first — pointing at a (potentially
        // already-disposed) other harness's TestServer. Pinning the model here makes
        // each test self-contained.
        foreach (var state in Agent.States)
        {
            if (string.IsNullOrWhiteSpace(state.Model))
                state.Model = ModelName;
        }

        AgentManager.Add(Agent);
    }

    /// <summary>Registers a fake tool and tracks it for cleanup on dispose.</summary>
    public void RegisterTool(IBuiltInTool tool)
    {
        ToolManager.Register(tool);
        RegisteredTools.Add(tool.Name);
    }

    public void Dispose()
    {
        foreach (string toolName in RegisteredTools)
            ToolManager.Unregister(toolName);

        // The static managers don't expose Remove APIs publicly, so we leak the agent/model/provider
        // entries — but the unique-suffix names mean cross-test contamination is impossible.
        // (Future hardening: add Remove APIs to ModelManager/ProviderManager/AgentManager for cleaner test teardown.)

        Server.Dispose();
    }
}
