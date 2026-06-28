// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// T20 + T18a + T19: the per-step system message auto-renders the allowed tools (name + description +
/// input format), the legal transition signals for the state, and the required JSON step contract — so
/// authors don't hand-copy any of it into [[_system]].
/// </summary>
public class ToolRenderingTests
{
    [Fact]
    public async Task SystemMessage_RendersToolDescription_Signals_AndJsonContract()
    {
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", Array.Empty<(string, string)>(), "done") },
            _ => AgentBuilder.FromText(AgentTextWithTool())!);
        harness.RegisterTool(new EchoTool());

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        string request = harness.Requests.First();

        // T20: the tool's description + input format is injected (not just its name).
        request.Should().Contain("echo-tool: Echoes the input");
        request.Should().Contain("Input: a plain string");
        // T19: the JSON step contract is auto-appended.
        request.Should().Contain("RESPONSE FORMAT");
        // T18a: the legal signals for this state are auto-injected.
        request.Should().Contain("Valid signals from this state: DONE");
    }

    [Fact]
    public async Task SystemMessage_NoTools_SaysNoneAvailable()
    {
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", Array.Empty<(string, string)>(), "done") },
            _ => AgentBuilder.FromText(AgentTextNoTools())!);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        harness.Requests.First().Should().Contain("none available");
    }

    private static string AgentTextWithTool() => @"
[[information]]
name = unused

[[loop]]
entry = work

[[state.work]]
description = work state
tools = echo-tool

[[_state.work.instruction]]
Use the tool, then signal DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";

    private static string AgentTextNoTools() => @"
[[information]]
name = unused

[[loop]]
entry = work

[[state.work]]
description = work state

[[_state.work.instruction]]
Just signal DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";

    /// <summary>A built-in tool whose description includes an explicit input format (for T20).</summary>
    private sealed class EchoTool : IBuiltInTool
    {
        public string Name => "echo-tool";
        public string Description => "Echoes the input back. Input: a plain string.";

        public Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
            => Task.FromResult(new ToolCallResult { ToolName = Name, Output = input });
    }
}
