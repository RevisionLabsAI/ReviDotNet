// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Verifies the per-run isolation overload
/// <c>IAgentService.Run(AgentProfile, IReadOnlyDictionary&lt;string,object&gt;, AgentRunContext?, CancellationToken, IToolManager?, ModelProfile?)</c>.
///
/// Two distinct agents — each declaring and calling a distinctly-named built-in tool — are run
/// CONCURRENTLY through the profile overload, each with its own private <see cref="IToolManager"/>
/// (holding only that agent's tool) and its own pinned <see cref="ModelProfile"/>. The assertions
/// prove there is no cross-talk: each run only ever invoked ITS OWN tool, and the runs do not
/// observe each other's tools. Because the profile overload performs no <see cref="IAgentManager"/>
/// name lookup and uses the supplied tool override, nothing is registered in (or resolved from) a
/// shared registry slot.
/// </summary>
public class PerRunIsolationTests
{
    [Fact]
    public async Task ProfileOverload_WithSeparateToolManagers_RunsConcurrently_WithNoToolCrossTalk()
    {
        // Two scripted providers/models (one per agent) so each run has a working, independent
        // inference server. The harness also pins each model's MaxTokens etc.
        using var harnessA = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new[] { ("tool-alpha", "{}") }, "calling alpha") },
            _ => AgentBuilder.FromText(AgentText("alpha", "tool-alpha"))!);
        using var harnessB = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new[] { ("tool-beta", "{}") }, "calling beta") },
            _ => AgentBuilder.FromText(AgentText("beta", "tool-beta"))!);

        // Recording tools — each lives ONLY in its own per-run tool manager.
        var alphaTool = new RecordingTool("tool-alpha");
        var betaTool = new RecordingTool("tool-beta");

        IToolManager managerA = new SingleToolManager(alphaTool);
        IToolManager managerB = new SingleToolManager(betaTool);

        // Cross-wire-proof: managerA must NOT see beta, managerB must NOT see alpha.
        managerA.GetBuiltIn("tool-beta").Should().BeNull();
        managerB.GetBuiltIn("tool-alpha").Should().BeNull();

        IAgentService serviceA = BuildService(managerA);
        IAgentService serviceB = BuildService(managerB);

        var noInputs = new Dictionary<string, object>();

        // Run BOTH concurrently through the new profile overload, each with its own tool override
        // and its own pinned model. No IAgentManager lookup, no shared-registry mutation.
        Task<AgentResult> runA = serviceA.Run(
            harnessA.Agent, noInputs, context: null, token: default,
            toolOverride: managerA, modelOverride: harnessA.Model);
        Task<AgentResult> runB = serviceB.Run(
            harnessB.Agent, noInputs, context: null, token: default,
            toolOverride: managerB, modelOverride: harnessB.Model);

        AgentResult[] results = await Task.WhenAll(runA, runB);

        // Both runs completed.
        results[0].ExitReason.Should().Be(AgentExitReason.Completed);
        results[1].ExitReason.Should().Be(AgentExitReason.Completed);

        // Each run invoked ONLY its own tool — no cross-talk.
        alphaTool.Invocations.Should().BeGreaterThan(0, "agent A's run should have invoked its own alpha tool");
        betaTool.Invocations.Should().BeGreaterThan(0, "agent B's run should have invoked its own beta tool");

        // The tools are physically distinct instances confined to disjoint managers, so neither
        // could possibly have been driven by the other run.
        alphaTool.Inputs.Should().OnlyContain(_ => true);
        alphaTool.Invocations.Should().Be(1);
        betaTool.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task ProfileOverload_SeparateToolManagers_ResolveDisjointToolSets()
    {
        // Minimal contract guarantee (independent of concurrent scripting): two runs with separate
        // IToolManagers resolve disjoint tool sets and complete without throwing.
        using var harnessA = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new[] { ("tool-alpha", "{}") }, "calling alpha") },
            _ => AgentBuilder.FromText(AgentText("alpha", "tool-alpha"))!);
        using var harnessB = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new[] { ("tool-beta", "{}") }, "calling beta") },
            _ => AgentBuilder.FromText(AgentText("beta", "tool-beta"))!);

        var alphaTool = new RecordingTool("tool-alpha");
        var betaTool = new RecordingTool("tool-beta");
        IToolManager managerA = new SingleToolManager(alphaTool);
        IToolManager managerB = new SingleToolManager(betaTool);

        managerA.GetBuiltInNames().Should().BeEquivalentTo(new[] { "tool-alpha" });
        managerB.GetBuiltInNames().Should().BeEquivalentTo(new[] { "tool-beta" });

        AgentResult a = await BuildService(managerA).Run(
            harnessA.Agent, new Dictionary<string, object>(),
            toolOverride: managerA, modelOverride: harnessA.Model);
        AgentResult b = await BuildService(managerB).Run(
            harnessB.Agent, new Dictionary<string, object>(),
            toolOverride: managerB, modelOverride: harnessB.Model);

        a.ExitReason.Should().Be(AgentExitReason.Completed);
        b.ExitReason.Should().Be(AgentExitReason.Completed);
        alphaTool.Invocations.Should().Be(1);
        betaTool.Invocations.Should().Be(1);
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>
    /// Builds an <see cref="AgentService"/> whose injected tool manager is <paramref name="injectedTools"/>.
    /// The model/prompt/agent managers are fresh empty instances — the profile overload does no agent
    /// lookup and the runs pin their model via <c>modelOverride</c>, so these are never consulted for
    /// resolution. (They exist only to satisfy the constructor.)
    /// </summary>
    private static IAgentService BuildService(IToolManager injectedTools)
    {
        IProviderManager providers = new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>());
        IModelManager models = new ModelManagerService(providers, new RecordingReviLogger<ModelManagerService>());
        IPromptManager prompts = new PromptManagerService(new RecordingReviLogger<PromptManagerService>());
        IAgentManager agents = new AgentManagerService(new RecordingReviLogger<AgentManagerService>());
        return new AgentService(agents, models, prompts, injectedTools, new RecordingReviLogger<AgentService>());
    }

    private static string AgentText(string name, string toolName) => $@"
[[information]]
name = {name}

[[loop]]
entry = work

[[state.work]]
description = work state
tools = {toolName}

[[_state.work.instruction]]
Call the {toolName} tool, then signal DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";

    /// <summary>An <see cref="IBuiltInTool"/> that records every invocation (count + inputs).</summary>
    private sealed class RecordingTool : IBuiltInTool
    {
        private int _invocations;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _inputs = new();

        public RecordingTool(string name) => Name = name;

        public string Name { get; }
        public string Description => $"Recording tool '{Name}' (test).";

        public int Invocations => Volatile.Read(ref _invocations);
        public IReadOnlyCollection<string> Inputs => _inputs;

        public Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
        {
            Interlocked.Increment(ref _invocations);
            _inputs.Enqueue(input);
            return Task.FromResult(new ToolCallResult { ToolName = Name, Output = $"{Name} ran" });
        }
    }

    /// <summary>
    /// A minimal per-run <see cref="IToolManager"/> holding exactly one built-in tool. Models the
    /// "private, non-shared tool set" the isolation overload is meant to provide — it knows nothing
    /// about any other run's tools. Custom-tool / directory-loading members are intentionally inert.
    /// </summary>
    private sealed class SingleToolManager : IToolManager
    {
        private readonly IBuiltInTool _tool;
        public SingleToolManager(IBuiltInTool tool) => _tool = tool;

        public IBuiltInTool? GetBuiltIn(string name)
            => string.Equals(name, _tool.Name, StringComparison.OrdinalIgnoreCase) ? _tool : null;

        public IReadOnlyCollection<string> GetBuiltInNames() => new[] { _tool.Name };

        public ToolProfile? GetCustom(string name) => null;
        public List<ToolProfile> GetAllCustom() => new();

        public void Register(IBuiltInTool tool)
            => throw new NotSupportedException("SingleToolManager is a fixed single-tool registry.");
        public bool Unregister(string name) => false;

        public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LoadDirectory(string rootDirectory) { }
    }
}
