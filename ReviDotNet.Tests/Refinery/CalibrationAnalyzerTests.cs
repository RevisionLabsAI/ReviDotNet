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
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// Unit tests for <see cref="CalibrationAnalyzer.AnalyzeAsync(string, string?, CancellationToken)"/>: joining
/// score cards' <see cref="FactCheckerDetermination"/> to a ground-truth map, per-confidence-bucket accuracy,
/// the Expected Calibration Error, and the monotonic-accuracy flag. The store is a hand-rolled fake that also
/// implements <see cref="IScoreCardSource"/> to surface canned cards + ground truth.
/// </summary>
public class CalibrationAnalyzerTests
{
    private static ScoreCard Card(string scenarioId, string agent, string winner, int confidence) => new()
    {
        ScenarioId = scenarioId,
        AgentName = agent,
        FactCheckerDetermination = new FactCheckerDetermination { Winner = winner, Confidence = confidence }
    };

    [Fact]
    public async Task AnalyzeAsync_ComputesBucketAccuracy_AndCorrectAndTotalCounts()
    {
        FakeStore store = new();
        store.GroundTruth["s1"] = "Alice";
        store.GroundTruth["s2"] = "Alice";
        store.GroundTruth["s3"] = "Alice";

        // Confidence 5: 2 runs, 1 correct -> accuracy 0.5
        store.Cards.Add(Card("s1", "fc", "Alice", 5));   // correct
        store.Cards.Add(Card("s2", "fc", "Bob", 5));     // wrong
        // Confidence 3: 1 run, 1 correct -> accuracy 1.0
        store.Cards.Add(Card("s3", "fc", "Alice", 3));   // correct

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc");

        report.TotalRuns.Should().Be(3);
        report.CalibratedRuns.Should().Be(2);

        report.Buckets.Should().HaveCount(2);
        ConfidenceBucket b5 = report.Buckets.Single(b => b.ConfidenceLevel == 5);
        b5.RunCount.Should().Be(2);
        b5.CorrectCount.Should().Be(1);
        b5.Accuracy.Should().BeApproximately(0.5, 1e-9);

        ConfidenceBucket b3 = report.Buckets.Single(b => b.ConfidenceLevel == 3);
        b3.RunCount.Should().Be(1);
        b3.CorrectCount.Should().Be(1);
        b3.Accuracy.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task AnalyzeAsync_ComputesEce_AsRunWeightedAbsoluteGap()
    {
        FakeStore store = new();
        store.GroundTruth["s1"] = "Alice";
        store.GroundTruth["s2"] = "Alice";
        store.GroundTruth["s3"] = "Alice";

        // Confidence 5 (expected 0.9): 2 runs, 1 correct -> acc 0.5, gap 0.4, weight 2/3
        store.Cards.Add(Card("s1", "fc", "Alice", 5));
        store.Cards.Add(Card("s2", "fc", "Bob", 5));
        // Confidence 3 (expected 0.5): 1 run, 1 correct -> acc 1.0, gap 0.5, weight 1/3
        store.Cards.Add(Card("s3", "fc", "Alice", 3));

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc");

        // ECE = (2/3)*|0.5 - 0.9| + (1/3)*|1.0 - 0.5| = (2/3)*0.4 + (1/3)*0.5
        double expected = (2.0 / 3.0) * 0.4 + (1.0 / 3.0) * 0.5;
        report.Ece.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsMonotonic_WhenAccuracyRisesWithConfidence()
    {
        FakeStore store = new();
        // bucket 2: 0.0 accuracy ; bucket 4: 1.0 accuracy -> monotonic (non-decreasing)
        store.GroundTruth["s1"] = "Alice";
        store.GroundTruth["s2"] = "Alice";
        store.Cards.Add(Card("s1", "fc", "Bob", 2));     // wrong, conf 2
        store.Cards.Add(Card("s2", "fc", "Alice", 4));   // correct, conf 4

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc");

        report.MonotonicAccuracy.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsNonMonotonic_WhenHigherConfidenceScoresWorse()
    {
        FakeStore store = new();
        // bucket 2: 1.0 accuracy ; bucket 4: 0.0 accuracy -> NOT monotonic (drops as confidence rises)
        store.GroundTruth["s1"] = "Alice";
        store.GroundTruth["s2"] = "Alice";
        store.Cards.Add(Card("s1", "fc", "Alice", 2));   // correct, conf 2
        store.Cards.Add(Card("s2", "fc", "Bob", 4));     // wrong, conf 4

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc");

        report.MonotonicAccuracy.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_MatchesWinnerCaseInsensitively()
    {
        FakeStore store = new();
        store.GroundTruth["s1"] = "Alice";
        store.Cards.Add(Card("s1", "fc", "alice", 5));

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc");

        report.TotalRuns.Should().Be(1);
        report.CalibratedRuns.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_IgnoresCardsWithoutDeterminationOrGroundTruth()
    {
        FakeStore store = new();
        store.GroundTruth["s1"] = "Alice";
        store.Cards.Add(Card("s1", "fc", "Alice", 5));               // counted
        store.Cards.Add(new ScoreCard { ScenarioId = "s2", AgentName = "fc" }); // no determination -> skipped
        store.Cards.Add(Card("s3", "fc", "Alice", 4));               // no ground truth for s3 -> skipped

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc");

        report.TotalRuns.Should().Be(1);
        report.Buckets.Should().ContainSingle();
        report.Buckets.Single().ConfidenceLevel.Should().Be(5);
    }

    [Fact]
    public async Task AnalyzeAsync_FiltersByAgentName_CaseInsensitive()
    {
        FakeStore store = new();
        store.GroundTruth["s1"] = "Alice";
        store.GroundTruth["s2"] = "Alice";
        store.Cards.Add(Card("s1", "FactChecker", "Alice", 5));
        store.Cards.Add(Card("s2", "OtherAgent", "Bob", 5));

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("factchecker");

        report.TotalRuns.Should().Be(1);
        report.CalibratedRuns.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_FiltersByAgentVersion_WhenSupplied()
    {
        FakeStore store = new();
        store.GroundTruth["s1"] = "Alice";
        store.GroundTruth["s2"] = "Alice";
        store.Cards.Add(Card("s1", "fc", "Alice", 5) with { AgentVersion = "v2" });
        store.Cards.Add(Card("s2", "fc", "Bob", 5) with { AgentVersion = "v1" });

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc", "v2");

        report.AgentVersion.Should().Be("v2");
        report.TotalRuns.Should().Be(1);
        report.CalibratedRuns.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_GroundTruthOverload_UsesCallerSuppliedMap()
    {
        FakeStore store = new();
        // store's own ground truth is empty; caller supplies it via the overload
        store.Cards.Add(Card("s1", "fc", "Alice", 5));
        Dictionary<string, string> truth = new() { ["s1"] = "Alice" };

        CalibrationReport report = await new CalibrationAnalyzer(store).AnalyzeAsync("fc", truth);

        report.TotalRuns.Should().Be(1);
        report.CalibratedRuns.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyStore_ReturnsEmptyReport()
    {
        CalibrationReport report = await new CalibrationAnalyzer(new FakeStore()).AnalyzeAsync("fc");

        report.TotalRuns.Should().Be(0);
        report.CalibratedRuns.Should().Be(0);
        report.Buckets.Should().BeEmpty();
        report.Ece.Should().Be(0);
        report.MonotonicAccuracy.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_StoreWithoutScoreCardSource_ReturnsEmptyReport()
    {
        CalibrationReport report = await new CalibrationAnalyzer(new PlainStore()).AnalyzeAsync("fc");

        report.TotalRuns.Should().Be(0);
        report.Buckets.Should().BeEmpty();
    }

    /// <summary>Canned store that also exposes score cards + ground truth via <see cref="IScoreCardSource"/>.</summary>
    private sealed class FakeStore : ICampaignStore, IScoreCardSource
    {
        public List<ScoreCard> Cards { get; } = [];
        public Dictionary<string, string> GroundTruth { get; } = [];

        public Task<IReadOnlyList<ScoreCard>> GetScoreCardsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScoreCard>>(Cards);

        public Task<IReadOnlyDictionary<string, string>> GetGroundTruthAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(GroundTruth);

        public Task SaveAsync(Campaign campaign, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Campaign?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<Campaign?>(null);
        public Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Campaign>>([]);
        public Task AppendLedgerAsync(LedgerEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LedgerEntry>>([]);
    }

    /// <summary>A store that does NOT implement <see cref="IScoreCardSource"/>.</summary>
    private sealed class PlainStore : ICampaignStore
    {
        public Task SaveAsync(Campaign campaign, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Campaign?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<Campaign?>(null);
        public Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Campaign>>([]);
        public Task AppendLedgerAsync(LedgerEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LedgerEntry>>([]);
    }
}
