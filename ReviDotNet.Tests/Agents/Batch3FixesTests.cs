// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Regression tests for the Batch 3 audit fixes (D22 per-state tuning binding, D30 cycle-limit self-loops).
/// </summary>
public class Batch3FixesTests
{
    // ── D22: sampling/tuning keys in [[_state.X.settings]] now bind (not just settings_* keys) ──

    [Fact]
    public void AgentState_InlineSettings_BindTuningAndSettingsKeys()
    {
        string text = @"
[[information]]
name = unused

[[loop]]
entry = draft

[[state.draft]]
description = draft state

[[_state.draft.settings]]
temperature = 0.2
top-p = 0.9
output-budget = 800

[[_loop]]
draft
  -> [end] [when: DONE]
";
        AgentProfile agent = AgentBuilder.FromText(text)!;
        AgentState draft = agent.States.Single(s => s.Name == "draft");

        draft.InlineSettings.Should().NotBeNull();
        draft.InlineSettings!.Temperature.Should().Be(0.2f);   // tuning_* key now binds
        draft.InlineSettings!.TopP.Should().Be(0.9f);          // tuning_* key now binds
        draft.InlineSettings!.OutputBudget.Should().Be(800);      // settings_* key still binds
    }

    // ── D30: a -> self transition now counts toward cycle-limit, bounding self-looping states ──

    [Fact]
    public async Task CycleLimit_BoundsSelfLoopingState()
    {
        string text = @"
[[information]]
name = unused

[[loop]]
entry = think

[[state.think]]
description = think state

[[state.think.guardrails]]
cycle-limit = 2

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
            new FakeAgentTurn("CONTINUE", new (string, string)[0], "5")
        };

        using var harness = new AgentTestHarness(script, _ => AgentBuilder.FromText(text)!);

        AgentResult result = await Agent.Run(harness.AgentName);

        // Without the fix the self-loop never increments the cycle count and runs unbounded;
        // with it, cycle-limit = 2 terminates the run.
        result.ExitReason.Should().Be(AgentExitReason.GuardrailViolation);
        result.GuardrailViolationMessage.Should().Contain("cycle limit");
    }
}
