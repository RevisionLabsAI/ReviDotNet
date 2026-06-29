// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>Deterministic tests for the typed knob mutators and the <see cref="KnobMutators"/> registry.</summary>
public class KnobMutatorTests
{
    // A sample .agent source carrying every knob the mutators target.
    private const string AgentSource =
        "[[information]]\n" +
        "name = sampleagent\n" +
        "\n" +
        "[[settings]]\n" +
        "preferred-models = claude-sonnet-4-6\n" +
        "temperature = 0.7\n" +
        "top-p = 0.95\n" +
        "\n" +
        "[[state.respond.guardrails]]\n" +
        "max-steps = 4\n" +
        "timeout = 300\n" +
        "\n" +
        "[[_system]]\n" +
        "You are a helpful assistant.\n" +
        "Follow the rules.\n" +
        "\n" +
        "[[_loop]]\n" +
        "respond\n";

    private static SuiteAggregate Scores(double passRate = 0.5, double qualityMean = 5.0) => new()
    {
        InvariantPassRate = passRate,
        QualityMean = qualityMean,
        QualityP10 = 2.0,
        RunCount = 4,
        GatedRunCount = 4,
        InvariantPassRateById = new Dictionary<string, double> { ["G-1"] = 0.0 }
    };

    private static ScoreCard Card(
        string id = "G-1",
        bool passed = false,
        string evidence = "answer was not grounded in the provided context",
        string outcome = "DONE") => new()
    {
        ScenarioId = "s1",
        AgentName = "sampleagent",
        Outcome = outcome,
        Invariants = [new InvariantResult { Id = id, Passed = passed, Severity = InvariantSeverity.High, Evidence = evidence }],
        Quality = new QualityScore { Overall = 4, Rationale = "weak", Facets = [new FacetScore("Grounding", 2, "ungrounded")] }
    };

    // ── Registry ───────────────────────────────────────────────────────────────

    [Fact]
    public void All_ReturnsOneOfEachMutator()
    {
        IReadOnlyList<ICandidateMutator> all = KnobMutators.All();

        all.Should().HaveCount(3);
        all.Select(m => m.KnobType).Should().BeEquivalentTo(["sampling", "guardrail", "system-prompt"]);
        all.Should().ContainItemsAssignableTo<ICandidateMutator>();
    }

    // ── SamplingMutator ──────────────────────────────────────────────────────────

    [Fact]
    public void Sampling_LowersTemperatureByOneTenth_WhenWeaknessPresent()
    {
        SamplingMutator m = new();

        Proposal? p = m.Mutate("sampleagent", AgentSource, Scores(), [Card()]);

        p.Should().NotBeNull();
        p!.KnobType.Should().Be("sampling");
        p.RevisedContent.Should().Contain("temperature = 0.6");
        p.RevisedContent.Should().NotContain("temperature = 0.7");
        p.Diff.Should().Contain("0.6");
        // Only the temperature line changed — top-p, max-steps and the system block are untouched.
        p.RevisedContent.Should().Contain("top-p = 0.95");
        p.RevisedContent.Should().Contain("max-steps = 4");
        // Exactly one line value changed: one removed (old) + one added (new).
        DiffCounts(AgentSource, p.RevisedContent).Should().Be((1, 1));
    }

    [Fact]
    public void Sampling_ReturnsNull_WhenNoTemperatureLine()
    {
        const string noTemp =
            "[[information]]\nname = x\n\n[[settings]]\npreferred-models = m\n\n[[_system]]\nHi.\n";
        SamplingMutator m = new();

        m.Mutate("x", noTemp, Scores(), [Card()]).Should().BeNull();
    }

    [Fact]
    public void Sampling_ReturnsNull_WhenTemperatureAlreadyZero()
    {
        string src = AgentSource.Replace("temperature = 0.7", "temperature = 0.0");
        SamplingMutator m = new();

        m.Mutate("sampleagent", src, Scores(), [Card()]).Should().BeNull();
    }

    [Fact]
    public void Sampling_ReturnsNull_WhenNoWeakness()
    {
        SamplingMutator m = new();

        // Perfect pass-rate and quality, all invariants passing ⇒ nothing to fix.
        m.Mutate("sampleagent", AgentSource, Scores(passRate: 1.0, qualityMean: 9.5), [Card(passed: true, evidence: "ok")])
            .Should().BeNull();
    }

    // ── GuardrailMutator ────────────────────────────────────────────────────────

    [Fact]
    public void Guardrail_RaisesMaxSteps_WhenTerminationFailurePresent()
    {
        GuardrailMutator m = new();
        ScoreCard loopCard = Card(id: "G-2", evidence: "agent hit the step limit and did not terminate");

        Proposal? p = m.Mutate("sampleagent", AgentSource, Scores(), [loopCard]);

        p.Should().NotBeNull();
        p!.KnobType.Should().Be("guardrail");
        // 4 + ceil(4*0.25)=1 ⇒ 5.
        p.RevisedContent.Should().Contain("max-steps = 5");
        p.RevisedContent.Should().NotContain("max-steps = 4");
        // Exactly one line value changed: one removed (old) + one added (new).
        DiffCounts(AgentSource, p.RevisedContent).Should().Be((1, 1));
    }

    [Fact]
    public void Guardrail_ReturnsNull_WhenNoTerminationSignal()
    {
        GuardrailMutator m = new();
        ScoreCard plain = Card(id: "G-3", evidence: "answer lacked a citation");

        m.Mutate("sampleagent", AgentSource, Scores(), [plain]).Should().BeNull();
    }

    [Fact]
    public void Guardrail_ReturnsNull_WhenNoMaxStepsLine()
    {
        const string noSteps =
            "[[information]]\nname = x\n\n[[_system]]\nHi.\n";
        GuardrailMutator m = new();
        ScoreCard loopCard = Card(evidence: "loop detected; never terminated");

        m.Mutate("x", noSteps, Scores(), [loopCard]).Should().BeNull();
    }

    // ── SystemPromptMutator ──────────────────────────────────────────────────────

    [Fact]
    public void SystemPrompt_AppendsCorrectiveClause_FromTopFailingInvariant()
    {
        SystemPromptMutator m = new();

        Proposal? p = m.Mutate("sampleagent", AgentSource, Scores(),
            [Card(evidence: "the answer was not grounded in the provided context")]);

        p.Should().NotBeNull();
        p!.KnobType.Should().Be("system-prompt");
        p.RevisedContent.Should().Contain("Always ground claims in the provided context");
        // Additive only: the original system text and other sections survive verbatim.
        p.RevisedContent.Should().Contain("You are a helpful assistant.");
        p.RevisedContent.Should().Contain("[[_loop]]");
        p.RevisedContent.Should().Contain("temperature = 0.7");
        // Exactly one line added, none removed.
        var (added, removed) = DiffCounts(AgentSource, p.RevisedContent);
        added.Should().Be(1);
        removed.Should().Be(0);
    }

    [Fact]
    public void SystemPrompt_ReturnsNull_WhenNoSystemBlock()
    {
        const string noSys =
            "[[information]]\nname = x\n\n[[settings]]\ntemperature = 0.5\n";
        SystemPromptMutator m = new();

        m.Mutate("x", noSys, Scores(), [Card()]).Should().BeNull();
    }

    [Fact]
    public void SystemPrompt_ReturnsNull_WhenNoFailingInvariants()
    {
        SystemPromptMutator m = new();

        m.Mutate("sampleagent", AgentSource, Scores(passRate: 1.0), [Card(passed: true, evidence: "ok")])
            .Should().BeNull();
    }

    [Fact]
    public void SystemPrompt_ReturnsNull_WhenClauseAlreadyPresent()
    {
        SystemPromptMutator m = new();

        // First application adds the clause; a second pass on the result must be a no-op.
        Proposal? first = m.Mutate("sampleagent", AgentSource, Scores(),
            [Card(evidence: "not grounded in context")]);
        first.Should().NotBeNull();

        m.Mutate("sampleagent", first!.RevisedContent, Scores(), [Card(evidence: "not grounded in context")])
            .Should().BeNull();
    }

    // ── Diff helpers (count changed lines via simple set difference of split lines) ──

    private static (int Added, int Removed) DiffCounts(string a, string b)
    {
        string[] al = Split(a);
        string[] bl = Split(b);
        var aSet = new List<string>(al);
        var bSet = new List<string>(bl);

        int removed = al.Count(line => !RemoveOnce(bSet, line));
        // Recompute for added using fresh lists.
        aSet = new List<string>(al);
        int added = bl.Count(line => !RemoveOnce(aSet, line));
        return (added, removed);
    }

    private static bool RemoveOnce(List<string> bag, string item) => bag.Remove(item);

    private static string[] Split(string s) => s.Replace("\r\n", "\n").Split('\n');
}
