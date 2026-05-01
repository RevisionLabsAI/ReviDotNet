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
/// Verifies cost-budget enforcement. The runner refuses an LLM call whose projected cost
/// would exceed either the state-level <c>cost-budget</c> or the run-wide <c>cost-budget</c>,
/// terminating with <see cref="AgentExitReason.BudgetExceeded"/> and the partial output it
/// has already accumulated.
/// </summary>
public class AgentRunnerCostBudgetTests
{
    [Fact]
    public async Task StateLevelBudget_ExceededByProjectedCost_TerminatesGracefullyWithLastContent()
    {
        // Set rates such that the projected first-call cost (~$0.0025 with MaxTokens=200 at $10/M)
        // already exceeds the state budget of $0.0001. The runner refuses the very first LLM call.
        string text = $@"
[[information]]
name = unused

[[loop]]
entry = think

[[state.think]]
description = think state

[[state.think.guardrails]]
cost-budget = 0.0001

[[_state.think.instruction]]
Emit DONE.

[[_loop]]
think
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("DONE", new (string, string)[0], "should not run")
        };

        using var harness = new AgentTestHarness(
            script,
            _ => AgentBuilder.FromText(text)!,
            costPerMillionInputTokens: 10m,
            costPerMillionOutputTokens: 10m);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.BudgetExceeded);
        result.GuardrailViolationMessage.Should().Contain("cost-budget");
        // No LLM call ran, so FinalOutput is null (no _lastContent accumulated).
        result.TotalSteps.Should().Be(0);
    }

    [Fact]
    public async Task RunLevelBudget_AccumulatesAcrossStatesAndTerminates()
    {
        // Run budget is 0.005 USD; each call costs ~0.001 (200 tokens at $5/M).
        // First call: projected 0.001 — under 0.005, runs (actual cost ~0.0015).
        // Second call: spent ~0.0015, projected next ~0.001 → projected total 0.0025, runs.
        // We want around 5 calls before run budget projects to exceed.
        string text = $@"
[[information]]
name = unused

[[loop]]
entry = think

[[settings]]
cost-budget = 0.005

[[state.think]]
description = think state

[[_state.think.instruction]]
Emit CONTINUE.

[[_loop]]
think
  -> self [when: CONTINUE]
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "1"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "2"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "3"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "4"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "5"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "6"),
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "7")
        };

        using var harness = new AgentTestHarness(
            script,
            _ => AgentBuilder.FromText(text)!,
            costPerMillionInputTokens: 5m,
            costPerMillionOutputTokens: 5m);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.BudgetExceeded);
        result.GuardrailViolationMessage.Should().Contain("Run cost-budget");
        // We should have made at least one call before termination — partial output should be set.
        result.FinalOutput.Should().NotBeNull();
    }

    [Fact]
    public async Task NoBudgetConfigured_NeverTerminatesEarly()
    {
        string text = $@"
[[information]]
name = unused

[[loop]]
entry = work

[[state.work]]
description = work state

[[_state.work.instruction]]
Emit DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("DONE", new (string, string)[0], "ok")
        };

        // Even with cost rates, no budget configured anywhere should mean unbounded operation.
        using var harness = new AgentTestHarness(
            script,
            _ => AgentBuilder.FromText(text)!,
            costPerMillionInputTokens: 1000m,
            costPerMillionOutputTokens: 1000m);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("ok");
    }

    [Fact]
    public async Task ModelWithoutCostRates_ContributesZeroCost()
    {
        // State budget set, but the model has no cost rates → projection returns 0,
        // budget never trips, agent completes normally.
        string text = $@"
[[information]]
name = unused

[[loop]]
entry = work

[[state.work]]
description = work state

[[state.work.guardrails]]
cost-budget = 0.001

[[_state.work.instruction]]
Emit DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("DONE", new (string, string)[0], "ok")
        };

        // Note: no cost-per-million-token rates supplied → harness leaves them null.
        using var harness = new AgentTestHarness(
            script,
            _ => AgentBuilder.FromText(text)!);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
    }
}
