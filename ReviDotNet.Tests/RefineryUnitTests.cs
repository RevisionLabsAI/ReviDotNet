// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>Deterministic tests for the Refinery engine's pure scoring/aggregation/trace logic.</summary>
public class RefineryUnitTests
{
    private sealed class FakeChecker(string id, bool passed) : IInvariantChecker
    {
        public string Id => id;
        public string Description => "fake";
        public InvariantSeverity Severity => InvariantSeverity.High;
        public InvariantResult Check(AgentTrace trace, Scenario scenario) =>
            passed ? InvariantResult.Pass(this, "ok") : InvariantResult.Fail(this, "nope");
    }

    private static AgentTrace TraceWithTools(params string[] toolNames)
    {
        List<TraceEvent> events = [];
        foreach (string t in toolNames)
            events.Add(new TraceEvent { Type = TraceEventTypes.ToolCall, Object2 = $"\"{t}\"" });
        return new AgentTrace { SessionId = "s1", AgentName = "agent", ExitReason = "Completed", Events = events };
    }

    private static Scenario Scn(params string[] expectedInvariants) =>
        new() { Id = "scn-1", AgentName = "agent", ExpectedInvariants = expectedInvariants };

    // ── StructuralScorer ────────────────────────────────────────────────

    [Fact]
    public void StructuralScorer_RunsExpectedCheckers_AndReportsPassFail()
    {
        AgentTrace trace = TraceWithTools("search");
        IInvariantChecker[] checkers = [new FakeChecker("A", true), new FakeChecker("B", false), new FakeChecker("C", true)];

        IReadOnlyList<InvariantResult> results = StructuralScorer.Score(trace, Scn("A", "B"), checkers);

        results.Should().HaveCount(2);
        results.Single(r => r.Id == "A").Passed.Should().BeTrue();
        results.Single(r => r.Id == "B").Passed.Should().BeFalse();
    }

    [Fact]
    public void StructuralScorer_NoExpected_RunsAllCheckers()
    {
        IInvariantChecker[] checkers = [new FakeChecker("A", true), new FakeChecker("B", true)];
        StructuralScorer.Score(TraceWithTools(), Scn(), checkers).Should().HaveCount(2);
    }

    // ── ScoreCard verdict ───────────────────────────────────────────────

    [Fact]
    public void ScoreCard_Verdict_FailsWhenAnyInvariantFails()
    {
        ScoreCard pass = new() { ScenarioId = "s", AgentName = "a", Invariants = [new() { Id = "A", Passed = true }] };
        ScoreCard fail = new()
        {
            ScenarioId = "s", AgentName = "a",
            Invariants = [new() { Id = "A", Passed = true }, new() { Id = "B", Passed = false }]
        };
        pass.Verdict.Should().Be(RunVerdict.Pass);
        fail.Verdict.Should().Be(RunVerdict.Fail);
    }

    // ── AgentTrace helpers ──────────────────────────────────────────────

    [Fact]
    public void AgentTrace_ToolCallsNamed_MatchesByName()
    {
        AgentTrace trace = TraceWithTools("search", "scrape", "search");
        trace.CalledTool("search").Should().BeTrue();
        trace.CalledTool("invoke_agent").Should().BeFalse();
        trace.ToolCallsNamed("search").Should().HaveCount(2);
        trace.ToolCalls.Should().HaveCount(3);
    }

    // ── Aggregator ──────────────────────────────────────────────────────

    [Fact]
    public void Aggregator_ComputesPassRateAndQuality()
    {
        List<ScoreCard> cards =
        [
            Card(passed: true, quality: 8),
            Card(passed: true, quality: 6),
            Card(passed: false, quality: 4),
            Card(passed: true, quality: 10)
        ];

        SuiteAggregate agg = Aggregator.Aggregate(cards);

        agg.RunCount.Should().Be(4);
        agg.InvariantPassRate.Should().BeApproximately(0.75, 1e-9);
        agg.QualityMean.Should().BeApproximately(7.0, 1e-9);
        agg.QualityP10.Should().BeLessThan(agg.QualityMean); // p10 below the mean for this spread
    }

    [Fact]
    public void Aggregator_Percentile_Interpolates()
    {
        List<double> sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Aggregator.Percentile(sorted, 0).Should().Be(1);
        Aggregator.Percentile(sorted, 100).Should().Be(10);
        Aggregator.Percentile(sorted, 50).Should().BeApproximately(5.5, 1e-9);
        Aggregator.Percentile([], 50).Should().Be(0);
    }

    private static ScoreCard Card(bool passed, int quality) => new()
    {
        ScenarioId = "s",
        AgentName = "a",
        Invariants = [new() { Id = "A", Passed = passed }],
        Quality = new QualityScore { Overall = quality },
        Efficiency = new EfficiencyMetrics { LatencyMs = 100, CostUsd = 0.01m }
    };

    // ── AgentTraceBuilder tag parsing (internal) ────────────────────────

    [Fact]
    public void AgentTraceBuilder_ParseTags_ExtractsSessionStateDepth()
    {
        (string? session, string? state, int? depth) =
            AgentTraceBuilder.ParseTags("agent:chatbot agent-session:abc123 agent-step:tool-call agent-state:respond agent-cycle:0 agent-depth:0");

        session.Should().Be("abc123");
        state.Should().Be("respond");
        depth.Should().Be(0);

        AgentTraceBuilder.ParseTags(null).Should().Be((null, null, null));
    }

    [Fact]
    public void AgentTraceBuilder_Build_UsesResultSession_SumsAllTokensIncludingSubAgent()
    {
        DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
        List<RlogEvent> events =
        [
            new() { Identifier = "agent-run-start", ParentId = null, Tags = "agent:root agent-session:root-sess agent-step:start agent-depth:0", Timestamp = T(0) },
            new() { Identifier = TraceEventTypes.LlmResponse, Tags = "agent:root agent-session:root-sess agent-state:respond agent-depth:0", Object2 = "{\"inputTokens\":100,\"outputTokens\":50,\"model\":\"m\"}", Timestamp = T(1) },
            new() { Identifier = TraceEventTypes.ToolCall, Tags = "agent:root agent-session:root-sess agent-state:respond agent-depth:0", Object2 = "\"invoke_agent\"", Timestamp = T(2) },
            // a sub-agent run: different session, depth 1, parented under the root's tool-call
            new() { Identifier = "agent-run-start", ParentId = "tc-1", Tags = "agent:fact-checker agent-session:sub-sess agent-step:start agent-depth:1", Timestamp = T(3) },
            new() { Identifier = TraceEventTypes.LlmResponse, Tags = "agent:fact-checker agent-session:sub-sess agent-depth:1", Object2 = "{\"inputTokens\":20,\"outputTokens\":10,\"model\":\"m\"}", Timestamp = T(4) },
        ];
        AgentResult result = new()
        {
            FinalOutput = "done",
            ExitReason = AgentExitReason.Completed,
            TotalSteps = 2,
            SessionId = "root-sess",
            Cost = 0.05m,
            StateHistory = ["respond"]
        };

        AgentTrace trace = AgentTraceBuilder.Build(events, "root", result);

        trace.SessionId.Should().Be("root-sess");          // from the result, not guessed
        trace.Completed.Should().BeTrue();
        trace.InputTokens.Should().Be(120);                // root 100 + sub-agent 20
        trace.OutputTokens.Should().Be(60);                // root 50 + sub-agent 10
        trace.CostUsd.Should().Be(0.05m);
        trace.CalledTool("invoke_agent").Should().BeTrue();
        trace.Events.Should().HaveCount(5);                // root + sub-agent events both included
    }
}
