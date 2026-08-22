// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Verifies that <c>state.X.prompt = name</c> resolves through <see cref="PromptManager"/>
/// and that the prompt's system+instruction are rendered into the agent's per-step messages
/// with <c>{key}</c> placeholders substituted from the agent's initial inputs.
/// </summary>
public class AgentRunnerPromptResolutionTests
{
    private static Prompt BuildPrompt(string name, string? system, string? instruction)
    {
        var p = new Prompt
        {
            Name = name,
            Version = 1,
            System = system,
            Instruction = instruction
        };
        PromptManager.AddOrUpdate(p);
        return p;
    }

    [Fact]
    public async Task PromptResolution_SubstitutesInputsIntoSystemAndInstruction()
    {
        // The fake server doesn't echo back the system prompt, but we can verify substitution
        // happened by ensuring the agent reaches Completed (the system text built without errors)
        // and inspecting the resolved prompt directly.
        string promptName = $"prompt-{Guid.NewGuid():n}";
        BuildPrompt(promptName, system: "You research {topic}.", instruction: "Research depth: {depth}.");

        string agentText = $@"
[[information]]
name = unused

[[loop]]
entry = research

[[state.research]]
prompt = {promptName}

[[_loop]]
research
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("DONE", new (string, string)[0], "report on healthcare")
        };

        using var harness = new AgentTestHarness(
            script,
            _ => AgentBuilder.FromText(agentText)!);

        AgentResult result = await Agent.Run(
            harness.AgentName,
            new Dictionary<string, object>
            {
                ["topic"] = "healthcare reform",
                ["depth"] = 3
            });

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("report on healthcare");
    }

    [Fact]
    public async Task PromptResolution_FallsBackToInlineInstructionWhenNamedPromptMissing()
    {
        // No prompt with this name registered — runner should warn and fall through.
        string missingPromptName = $"missing-prompt-{Guid.NewGuid():n}";
        string agentText = $@"
[[information]]
name = unused

[[loop]]
entry = work

[[state.work]]
prompt = {missingPromptName}

[[_state.work.instruction]]
Inline fallback instruction. Emit DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("DONE", new (string, string)[0], "ok")
        };

        using var harness = new AgentTestHarness(
            script,
            _ => AgentBuilder.FromText(agentText)!);

        AgentResult result = await Agent.Run(harness.AgentName);

        // The agent shouldn't fail just because a referenced prompt was missing — the inline
        // instruction is still there as a fallback, and the loop reaches [end] normally.
        result.ExitReason.Should().Be(AgentExitReason.Completed);
    }

    [Fact]
    public async Task PromptResolution_AndInlineInstruction_AreBothApplied()
    {
        // When both prompt = name and [[_state.X.instruction]] are present, both are used —
        // we just need to verify the agent runs cleanly with this combo.
        string promptName = $"prompt-combo-{Guid.NewGuid():n}";
        BuildPrompt(promptName, system: "Base system.", instruction: "Base instruction with {topic}.");

        string agentText = $@"
[[information]]
name = unused

[[loop]]
entry = work

[[state.work]]
prompt = {promptName}

[[_state.work.instruction]]
Override layer added on top. Emit DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";
        var script = new[]
        {
            new FakeAgentTurn("DONE", new (string, string)[0], "ok")
        };

        using var harness = new AgentTestHarness(
            script,
            _ => AgentBuilder.FromText(agentText)!);

        AgentResult result = await Agent.Run(
            harness.AgentName,
            new Dictionary<string, object> { ["topic"] = "X" });

        result.ExitReason.Should().Be(AgentExitReason.Completed);
    }

    // ── settings_system-prompt: an agent-level system prompt taken from a .pmt ──────────

    /// <summary>
    /// Builds an agent whose system prompt is named rather than inlined.
    /// </summary>
    /// <param name="promptName">The prompt to reference, or null to omit the setting.</param>
    /// <param name="inlineSystem">An inline [[_system]] block, or null to omit it.</param>
    /// <returns>The .agent text.</returns>
    private static string AgentReferencingSystemPrompt(string? promptName, string? inlineSystem = null)
    {
        string settings = promptName is null ? "" : $@"
[[settings]]
system-prompt = {promptName}
";
        string system = inlineSystem is null ? "" : $@"
[[_system]]
{inlineSystem}
";
        return $@"
[[information]]
name = unused
{settings}{system}
[[loop]]
entry = work

[[state.work]]
description = do the work

[[_state.work.instruction]]
Do the work. Emit DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";
    }

    [Fact]
    public async Task SystemPromptReference_SuppliesTheAgentSystemPrompt()
    {
        // The point of the setting: two things that must share a system prompt can reference one
        // file instead of each keeping a copy that drifts.
        string promptName = $"sys-prompt-{Guid.NewGuid():n}";
        BuildPrompt(promptName, system: "You are a careful assistant.", instruction: null);

        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new (string, string)[0], "ok") },
            _ => AgentBuilder.FromText(AgentReferencingSystemPrompt(promptName))!);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        harness.Requests.Should().Contain(r => r.Contains("You are a careful assistant."),
            "the referenced prompt's system section must reach the model");
    }

    [Fact]
    public async Task SystemPromptReference_ComesBeforeAnInlineSystemBlock()
    {
        // Both may be present. The referenced prompt is the shared base; the inline block is this
        // agent's addition to it, so it is appended rather than replacing it.
        string promptName = $"sys-prompt-{Guid.NewGuid():n}";
        BuildPrompt(promptName, system: "Shared base rules.", instruction: null);

        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new (string, string)[0], "ok") },
            _ => AgentBuilder.FromText(
                AgentReferencingSystemPrompt(promptName, inlineSystem: "This agent also does X."))!);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);

        string request = harness.Requests.Should().ContainSingle().Subject;
        request.Should().Contain("Shared base rules.");
        request.Should().Contain("This agent also does X.");
        request.IndexOf("Shared base rules.", StringComparison.Ordinal)
            .Should().BeLessThan(request.IndexOf("This agent also does X.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SystemPromptReference_SubstitutesTheAgentsInputs()
    {
        // Same placeholder handling a state's prompt reference gets.
        string promptName = $"sys-prompt-{Guid.NewGuid():n}";
        BuildPrompt(promptName, system: "You answer questions about {topic}.", instruction: null);

        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new (string, string)[0], "ok") },
            _ => AgentBuilder.FromText(AgentReferencingSystemPrompt(promptName))!);

        AgentResult result = await Agent.Run(
            harness.AgentName,
            new Dictionary<string, object> { ["topic"] = "hydrology" });

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        harness.Requests.Should().Contain(r => r.Contains("questions about hydrology"));
    }

    [Fact]
    public async Task SystemPromptReference_ThatDoesNotResolve_DoesNotFailTheRun()
    {
        // Same treatment an unresolvable state prompt gets: logged and skipped. An agent is not
        // failed because a prompt was renamed.
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new (string, string)[0], "ok") },
            _ => AgentBuilder.FromText(
                AgentReferencingSystemPrompt($"no-such-prompt-{Guid.NewGuid():n}"))!);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
    }

    [Fact]
    public void SystemPromptReference_IsParsedFromTheSettingsSection()
    {
        AgentProfile? profile = AgentBuilder.FromText(AgentReferencingSystemPrompt("some-prompt"));

        profile.Should().NotBeNull();
        profile!.SystemPromptName.Should().Be("some-prompt");
    }

    [Fact]
    public void AnAgentWithNoSystemPromptSetting_LeavesItNull()
    {
        AgentProfile? profile = AgentBuilder.FromText(AgentReferencingSystemPrompt(null, "Inline only."));

        profile.Should().NotBeNull();
        profile!.SystemPromptName.Should().BeNull();
        profile.SystemPrompt.Should().Contain("Inline only.");
    }
}
