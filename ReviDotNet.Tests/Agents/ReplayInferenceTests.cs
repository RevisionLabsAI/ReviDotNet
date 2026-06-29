// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Verifies the scripted-inference replay seam (<see cref="ReplayInference"/> /
/// <see cref="ScriptedInferenceHandler"/>). A tiny agent is run through the Wave-3a per-run
/// <c>modelOverride</c> overload with a scripted <see cref="ModelProfile"/> and <b>no live provider
/// configured anywhere</b> — every LLM call is served by the script. The run must complete with the
/// scripted final output, and running twice must produce identical results (deterministic replay).
/// </summary>
public class ReplayInferenceTests
{
    [Fact]
    public async Task ReplayModel_DrivesAgentToScriptedFinalOutput_WithNoLiveProvider()
    {
        const string finalAnswer = "the scripted final answer";
        var script = new List<ReplayTurn>
        {
            new() { Signal = "DONE", Content = finalAnswer, PromptTokens = 42, CompletionTokens = 7 },
        };

        AgentProfile agent = AgentBuilder.FromText(NoToolAgentText());

        // Build a service whose managers are all empty — no provider/model is registered, so the run
        // can ONLY succeed via the scripted modelOverride. (If replay leaked to live inference, the
        // run would fail to resolve any model.)
        IAgentService service = BuildBareService();

        ModelProfile replayModel = ReplayInference.BuildModel("__replay/t", script);

        AgentResult result = await service.Run(
            agent,
            new Dictionary<string, object>(),
            modelOverride: replayModel);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be(finalAnswer);
    }

    [Fact]
    public async Task ReplayModel_IsDeterministic_RunTwiceYieldsIdenticalResult()
    {
        const string finalAnswer = "deterministic output";
        List<ReplayTurn> Script() => new()
        {
            new() { Signal = "CONTINUE", Content = "thinking step", PromptTokens = 10, CompletionTokens = 3 },
            new() { Signal = "DONE", Content = finalAnswer, PromptTokens = 11, CompletionTokens = 4 },
        };

        AgentProfile agent = AgentBuilder.FromText(NoToolAgentText());

        // Fresh scripted model per run (each handler holds its own call counter) so the two runs are
        // truly independent — yet both consume the same script and must land identically.
        AgentResult first = await BuildBareService().Run(
            agent, new Dictionary<string, object>(),
            modelOverride: ReplayInference.BuildModel("__replay/t", Script()));

        AgentResult second = await BuildBareService().Run(
            agent, new Dictionary<string, object>(),
            modelOverride: ReplayInference.BuildModel("__replay/t", Script()));

        first.ExitReason.Should().Be(AgentExitReason.Completed);
        second.ExitReason.Should().Be(AgentExitReason.Completed);
        first.FinalOutput.Should().Be(finalAnswer);
        second.FinalOutput.Should().Be(first.FinalOutput);
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>
    /// An <see cref="AgentService"/> with empty manager registries. The profile overload does no agent
    /// lookup and the run pins its model via <c>modelOverride</c>, so nothing must be registered: this
    /// guarantees the test exercises replay alone, with no live inference fallback available.
    /// </summary>
    private static IAgentService BuildBareService()
    {
        IProviderManager providers = new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>());
        IModelManager models = new ModelManagerService(providers, new RecordingReviLogger<ModelManagerService>());
        IPromptManager prompts = new PromptManagerService(new RecordingReviLogger<PromptManagerService>());
        IAgentManager agents = new AgentManagerService(new RecordingReviLogger<AgentManagerService>());
        IToolManager tools = new EmptyToolManager();
        return new AgentService(agents, models, prompts, tools, new RecordingReviLogger<AgentService>());
    }

    /// <summary>A minimal single-state agent that uses no tools and simply signals DONE.</summary>
    private static string NoToolAgentText() => @"
[[information]]
name = replay-agent

[[loop]]
entry = work

[[state.work]]
description = work state

[[_state.work.instruction]]
Produce the answer, then signal DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";

    /// <summary>
    /// A minimal inert <see cref="IToolManager"/> holding no tools. The replay agents use no tools, so
    /// the run never resolves anything from here; it exists only to satisfy the service constructor
    /// (mirrors <c>PerRunIsolationTests</c>'s use of a tiny custom manager instead of the heavyweight
    /// <c>ToolManagerService</c>).
    /// </summary>
    private sealed class EmptyToolManager : IToolManager
    {
        public IBuiltInTool? GetBuiltIn(string name) => null;
        public IReadOnlyCollection<string> GetBuiltInNames() => Array.Empty<string>();
        public ToolProfile? GetCustom(string name) => null;
        public List<ToolProfile> GetAllCustom() => new();
        public void Register(IBuiltInTool tool) => throw new NotSupportedException("EmptyToolManager holds no tools.");
        public bool Unregister(string name) => false;
        public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LoadDirectory(string rootDirectory) { }
    }
}
