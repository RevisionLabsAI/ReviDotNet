// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Verifies graceful signal validation: when the LLM emits a signal not declared from the
/// current state's loop transitions, the runner nudges the LLM with the valid set rather
/// than spinning silently. After repeated bad signals the run terminates with InvalidSignal.
/// </summary>
public class AgentRunnerSignalValidationTests
{
    private static AgentProfile BuildAgent(string modelName)
    {
        string text = $@"
[[information]]
name = unused

[[loop]]
entry = decide

[[state.decide]]
model = {modelName}

[[_state.decide.instruction]]
Emit READY when ready, ABORT to stop.

[[_loop]]
decide
  -> [end] [when: READY]
  -> [end] [when: ABORT]
";
        return AgentBuilder.FromText(text)!;
    }

    [Fact]
    public async Task UnknownSignal_NudgesLlmThenAcceptsValidSignalOnRetry()
    {
        var script = new[]
        {
            // First turn: emit a typo. Runner should nudge and re-prompt.
            new FakeAgentTurn("REDY", new (string, string)[0], "first try"),
            // Second turn: emit the correct signal.
            new FakeAgentTurn("READY", new (string, string)[0], "fixed it")
        };

        using var harness = new AgentTestHarness(script, BuildAgent);
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("fixed it");
        result.TotalSteps.Should().Be(2);
    }

    [Fact]
    public async Task PersistentBadSignals_TerminateWithInvalidSignalReason()
    {
        // Three consecutive bad signals. The runner accepts up to MaxSignalCorrectionsPerActivation (2)
        // nudges before terminating.
        var script = new[]
        {
            new FakeAgentTurn("BAD1", new (string, string)[0], "first"),
            new FakeAgentTurn("BAD2", new (string, string)[0], "second"),
            new FakeAgentTurn("BAD3", new (string, string)[0], "third"),
            new FakeAgentTurn("BAD4", new (string, string)[0], "fourth — should not be reached")
        };

        using var harness = new AgentTestHarness(script, BuildAgent);
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.InvalidSignal);
        result.GuardrailViolationMessage.Should().Contain("unknown signals");
    }

    [Fact]
    public async Task NullSignal_DoesNotConsumeCorrectionBudget()
    {
        // The LLM emits no signal at all (null) for the first turn — should not increment the
        // correction counter, and the runner should keep going (existing behaviour for null signal).
        var script = new[]
        {
            new FakeAgentTurn(null, new (string, string)[0], "thinking"),
            new FakeAgentTurn("READY", new (string, string)[0], "decided")
        };

        using var harness = new AgentTestHarness(script, BuildAgent);
        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("decided");
    }
}
