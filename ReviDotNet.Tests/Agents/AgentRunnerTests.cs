// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

public class AgentRunnerTests
{
    private static AgentProfile BuildTwoStateAgent(string modelName)
    {
        string agentText = $@"
[[information]]
name = unused-overwritten-by-harness

[[loop]]
entry = think

[[state.think]]
description = Initial reasoning state
model = {modelName}

[[state.summarize]]
description = Final summary state
model = {modelName}

[[_state.think.instruction]]
Decide whether to summarize or stop. Emit READY when ready, ABORT to stop.

[[_state.summarize.instruction]]
Produce the final summary. Emit DONE when complete.

[[_loop]]
think
  -> summarize [when: READY]
  -> [end] [when: ABORT]
summarize
  -> [end] [when: DONE]
";
        return AgentBuilder.FromText(agentText)!;
    }

    [Fact]
    public async Task HappyPath_TwoStateMachine_ReachesEndAndReturnsFinalContent()
    {
        var script = new[]
        {
            // think state -> emit READY to advance
            new FakeAgentTurn("READY", new (string, string)[0], "Going to summarize"),
            // summarize state -> emit DONE to finish, content becomes FinalOutput
            new FakeAgentTurn("DONE", new (string, string)[0], "Final summary text")
        };

        using var harness = new AgentTestHarness(script, BuildTwoStateAgent);
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("Final summary text");
        result.StateHistory.Should().Equal(new[] { "think", "summarize" });
        result.TotalSteps.Should().Be(2);
    }

    [Fact]
    public async Task MaxStepsGuardrail_TerminatesGracefullyWithLastContent()
    {
        // An agent whose entry state has max-steps=2 but the LLM never emits a transition signal
        string agentText = $@"
[[information]]
name = unused

[[loop]]
entry = thinking

[[state.thinking]]
description = Loops without transitioning

[[state.thinking.guardrails]]
max-steps = 2

[[_state.thinking.instruction]]
Just keep emitting CONTINUE forever.

[[_loop]]
thinking
  -> self [when: CONTINUE]
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "step 1"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "step 2"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "should not be seen")
        };

        using var harness = new AgentTestHarness(script, _ => AgentBuilder.FromText(agentText)!);
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.GuardrailViolation);
        result.GuardrailViolationMessage.Should().Contain("max-steps");
        result.FinalOutput.Should().Be("step 2");
    }

    [Fact]
    public async Task ToolCall_ResultAppendedToHistoryAndAgentContinues()
    {
        string agentText = $@"
[[information]]
name = unused

[[loop]]
entry = act

[[state.act]]
description = Calls a fake tool then transitions
tools = noop-tool

[[_state.act.instruction]]
Call the tool, then emit DONE.

[[_loop]]
act
  -> [end] [when: DONE]
";
        string toolName = $"noop-tool";
        var script = new[]
        {
            new FakeAgentTurn("CONTINUE", new[] { (toolName, "{\"x\":1}") }, "calling tool"),
            new FakeAgentTurn("DONE", new (string, string)[0], "tool ran, now done")
        };

        using var harness = new AgentTestHarness(script, _ => AgentBuilder.FromText(agentText)!);
        harness.RegisterTool(new FakeBuiltInTool(toolName, "tool-output"));

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("tool ran, now done");
    }

    [Fact]
    public async Task ToolCall_DisallowedToolIsIgnored()
    {
        // The state's tools list does NOT include "blocked-tool"; the LLM tries to call it anyway.
        string agentText = $@"
[[information]]
name = unused

[[loop]]
entry = act

[[state.act]]
tools = allowed-tool

[[_state.act.instruction]]
Try to use any tool you want, then emit DONE.

[[_loop]]
act
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("CONTINUE", new[] { ("blocked-tool", "{}") }, "tried blocked"),
            new FakeAgentTurn("DONE", new (string, string)[0], "ok")
        };

        using var harness = new AgentTestHarness(script, _ => AgentBuilder.FromText(agentText)!);
        // Register blocked-tool so we'd KNOW if it ran; but the runner should refuse to dispatch it.
        bool blockedToolWasCalled = false;
        harness.RegisterTool(new FakeBuiltInTool("blocked-tool", _ => { blockedToolWasCalled = true; return ""; }));
        harness.RegisterTool(new FakeBuiltInTool("allowed-tool", "ok"));

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        blockedToolWasCalled.Should().BeFalse("disallowed tools must not be dispatched");
    }

    [Fact]
    public async Task LoopDetection_TerminatesWhenSlidingWindowRepeats()
    {
        // Two-state alternation: think -> summarize -> think -> summarize ... should trigger detection
        // once the traversal history has a repeated sub-sequence.
        string agentText = $@"
[[information]]
name = unused

[[loop]]
entry = think

[[state.think]]
description = ping

[[state.think.guardrails]]
loop-detection = true

[[state.summarize]]
description = pong

[[state.summarize.guardrails]]
loop-detection = true

[[_state.think.instruction]]
Always emit GO.

[[_state.summarize.instruction]]
Always emit BACK.

[[_loop]]
think
  -> summarize [when: GO]
summarize
  -> think [when: BACK]
";
        var script = new[]
        {
            new FakeAgentTurn("GO",   new (string, string)[0], "t1"),
            new FakeAgentTurn("BACK", new (string, string)[0], "s1"),
            new FakeAgentTurn("GO",   new (string, string)[0], "t2"),
            new FakeAgentTurn("BACK", new (string, string)[0], "s2"),
            new FakeAgentTurn("GO",   new (string, string)[0], "t3"),
            new FakeAgentTurn("BACK", new (string, string)[0], "s3")
        };

        using var harness = new AgentTestHarness(script, _ => AgentBuilder.FromText(agentText)!);
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.LoopDetected);
    }
}
