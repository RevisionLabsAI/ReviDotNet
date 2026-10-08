// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Pins how the runner counts state activations, detects loops, nests sub-agents and substitutes
/// inputs — each test here failed against the runner as it stood on 2026-10-06.
/// </summary>
public class AgentRunnerStateAccountingTests
{
    private static readonly (string, string)[] NoTools = [];

    [Fact]
    public async Task CycleLimit_CountsActivationsOfItsOwnStateOnly()
    {
        // Three states each entered once. The limit on the last state must not be consumed by the
        // activations of the two states before it.
        string text = @"
[[information]]
name = unused

[[loop]]
entry = first

[[state.first]]
description = first

[[state.second]]
description = second

[[state.third]]
description = third

[[state.third.guardrails]]
cycle-limit = 2

[[_loop]]
first
  -> second [when: NEXT]
second
  -> third [when: NEXT]
third
  -> [end] [when: DONE]
";
        FakeAgentTurn[] script =
        [
            new("NEXT", NoTools, "1"),
            new("NEXT", NoTools, "2"),
            new("DONE", NoTools, "3")
        ];

        using AgentTestHarness harness = new(script, _ => AgentBuilder.FromText(text)!);
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed, result.GuardrailViolationMessage);
        result.FinalOutput.Should().Be("3");
    }

    [Fact]
    public async Task LoopDetection_AllowsRepeatedRoundsThatDoDifferentWork()
    {
        // search -> analyse three times over, with a different query each round: a revisit, not a loop.
        string text = @"
[[information]]
name = unused

[[loop]]
entry = search

[[state.search]]
description = search
tools = lookup-tool

[[state.search.guardrails]]
loop-detection = true

[[state.analyse]]
description = analyse

[[state.analyse.guardrails]]
loop-detection = true

[[_loop]]
search
  -> analyse [when: FOUND]
analyse
  -> search [when: MORE]
  -> [end] [when: DONE]
";
        FakeAgentTurn[] script =
        [
            new("FOUND", [("lookup-tool", "first query")], "s1"),
            new("MORE", NoTools, "a1"),
            new("FOUND", [("lookup-tool", "second query")], "s2"),
            new("MORE", NoTools, "a2"),
            new("FOUND", [("lookup-tool", "third query")], "s3"),
            new("DONE", NoTools, "a3")
        ];

        using AgentTestHarness harness = new(script, _ => AgentBuilder.FromText(text)!);
        harness.RegisterTool(new FakeBuiltInTool("lookup-tool", "hit"));
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("a3");
    }

    [Fact]
    public async Task LoopDetection_StopsRoundsThatRepeatTheSameToolCalls()
    {
        string text = @"
[[information]]
name = unused

[[loop]]
entry = search

[[state.search]]
description = search
tools = lookup-tool

[[state.search.guardrails]]
loop-detection = true

[[state.analyse]]
description = analyse

[[state.analyse.guardrails]]
loop-detection = true

[[_loop]]
search
  -> analyse [when: FOUND]
analyse
  -> search [when: MORE]
  -> [end] [when: DONE]
";
        FakeAgentTurn[] script =
        [
            new("FOUND", [("lookup-tool", "same query")], "s1"),
            new("MORE", NoTools, "a1"),
            new("FOUND", [("lookup-tool", "same query")], "s2"),
            new("MORE", NoTools, "a2"),
            new("FOUND", [("lookup-tool", "same query")], "s3"),
            new("DONE", NoTools, "a3")
        ];

        using AgentTestHarness harness = new(script, _ => AgentBuilder.FromText(text)!);
        harness.RegisterTool(new FakeBuiltInTool("lookup-tool", "hit"));
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.LoopDetected);
        result.TotalSteps.Should().Be(4, "two identical rounds are enough to call it a loop");
    }

    [Fact]
    public async Task InvokeAgent_FromATopLevelRun_StartsTheSubAgentAtDepthOne()
    {
        RecordingAgentService subAgents = new();
        using AgentTestHarness harness = new(InvokeOnceScript(), _ => AgentBuilder.FromText(InvokingAgent(maxDepth: null))!);
        harness.RegisterTool(new InvokeAgentTool(new Lazy<IAgentService>(() => subAgents)));

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        subAgents.Depths.Should().Equal(1);
    }

    [Fact]
    public async Task InvokeAgent_WithMaxAgentDepthOne_AllowsOneLevelOfNesting()
    {
        RecordingAgentService subAgents = new();
        using AgentTestHarness harness = new(InvokeOnceScript(), _ => AgentBuilder.FromText(InvokingAgent(maxDepth: 1))!);
        harness.RegisterTool(new InvokeAgentTool(new Lazy<IAgentService>(() => subAgents)));

        await Agent.Run(harness.AgentName);

        subAgents.Depths.Should().Equal(1);
    }

    [Fact]
    public async Task InputSubstitution_TreatsDollarSequencesInAValueAsLiteralText()
    {
        string text = @"
[[information]]
name = unused

[[loop]]
entry = act

[[state.act]]
description = act

[[_state.act.instruction]]
Topic: {topic}

[[_loop]]
act
  -> [end] [when: DONE]
";
        FakeAgentTurn[] script = [new("DONE", NoTools, "ok")];

        using AgentTestHarness harness = new(script, _ => AgentBuilder.FromText(text)!);
        await Agent.Run(harness.AgentName, new Dictionary<string, object> { ["topic"] = "costs $0 or $$ today" });

        harness.Requests.Should().ContainSingle()
            .Which.Should().Contain("Topic: costs $0 or $$ today");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_DuringAToolCall_EndsTheRunAsCancelled(bool toolThrows)
    {
        // Whether the tool notices the cancellation and throws, or finishes anyway, the caller gets
        // the documented Cancelled result rather than an exception.
        string text = @"
[[information]]
name = unused

[[loop]]
entry = act

[[state.act]]
description = act
tools = cancelling-tool

[[_loop]]
act
  -> [end] [when: DONE]
";
        FakeAgentTurn[] script =
        [
            new(null, [("cancelling-tool", "")], "calling"),
            new("DONE", NoTools, "should not be reached")
        ];
        using CancellationTokenSource cancel = new();

        using AgentTestHarness harness = new(script, _ => AgentBuilder.FromText(text)!);
        harness.RegisterTool(new FakeBuiltInTool("cancelling-tool", _ =>
        {
            cancel.Cancel();
            if (toolThrows) cancel.Token.ThrowIfCancellationRequested();
            return "done";
        }));

        AgentResult result = await Agent.Run(harness.AgentName, inputs: null, cancel.Token);

        result.ExitReason.Should().Be(AgentExitReason.Cancelled);
        result.TotalSteps.Should().Be(1);
    }

    private static FakeAgentTurn[] InvokeOnceScript() =>
    [
        new(null, [("invoke_agent", "{\"agent\":\"child\",\"task\":\"go\"}")], "delegating"),
        new("DONE", NoTools, "finished")
    ];

    private static string InvokingAgent(int? maxDepth) => $@"
[[information]]
name = unused

[[loop]]
entry = act

[[state.act]]
description = act
tools = invoke_agent
{(maxDepth is null ? "" : $"\n[[state.act.guardrails]]\nmax-agent-depth = {maxDepth}\n")}
[[_loop]]
act
  -> [end] [when: DONE]
";

    /// <summary>Stands in for the agent service so a test can see the context a sub-agent is started with.</summary>
    private sealed class RecordingAgentService : IAgentService
    {
        /// <summary>The nesting depth of each sub-agent run requested, in order.</summary>
        public List<int> Depths { get; } = [];

        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs, AgentRunContext ctx, CancellationToken token = default)
        {
            lock (Depths) Depths.Add(ctx.Depth);
            return Task.FromResult(new AgentResult { FinalOutput = "child output", ExitReason = AgentExitReason.Completed });
        }

        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentResult> Run(AgentProfile profile, IReadOnlyDictionary<string, object> inputs, AgentRunContext? context = null, CancellationToken token = default, IToolManager? toolOverride = null, ModelProfile? modelOverride = null) => throw new NotSupportedException();
        public Task<AgentResult> Run(string agentName, string input, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string?> ToString(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string?> ToString(string agentName, string input, CancellationToken token = default) => throw new NotSupportedException();
    }
}
