// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using FluentAssertions;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>Deterministic tests for the pure <see cref="GatePolicy"/> acceptance function.</summary>
public class GatePolicyTests
{
    private static SuiteAggregate Agg(
        double passRate,
        double qualityP10,
        IReadOnlyDictionary<string, double>? byId = null) => new()
    {
        InvariantPassRate = passRate,
        QualityP10 = qualityP10,
        QualityMean = qualityP10,
        RunCount = 3,
        GatedRunCount = 3,
        InvariantPassRateById = byId ?? new Dictionary<string, double>()
    };

    [Fact]
    public void Decide_AcceptsWhenAllConditionsMet()
    {
        SuiteAggregate baselineTrain = Agg(1.0, 6.0);
        SuiteAggregate candTrain = Agg(1.0, 7.0);
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(1.0, 6.0);

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 2);

        d.Accept.Should().BeTrue();
        d.Reason.Should().Contain("Accepted");
    }

    [Fact]
    public void Decide_RejectsOnTrainInvariantRegression()
    {
        SuiteAggregate baselineTrain = Agg(1.0, 6.0);
        SuiteAggregate candTrain = Agg(0.8, 9.0); // pass rate dropped despite higher quality
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(1.0, 6.0);

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 5);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("Invariant regression on train");
    }

    [Fact]
    public void Decide_RejectsOnTrainInvariantIdDroppingToFailing()
    {
        // Aggregate pass rate holds (one id improves as another regresses) but a previously-passing id drops.
        var baseById = new Dictionary<string, double> { ["A"] = 1.0, ["B"] = 0.5 };
        var candById = new Dictionary<string, double> { ["A"] = 0.5, ["B"] = 1.0 };

        SuiteAggregate baselineTrain = Agg(0.75, 6.0, baseById);
        SuiteAggregate candTrain = Agg(0.75, 7.0, candById);
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(1.0, 6.0);

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 3);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("'A'");
    }

    [Fact]
    public void Decide_RejectsOnHeldOutInvariantRegression()
    {
        SuiteAggregate baselineTrain = Agg(1.0, 6.0);
        SuiteAggregate candTrain = Agg(1.0, 7.0);
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(0.9, 6.0); // held-out invariants regressed

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 4);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("Invariant regression on held-out");
    }

    [Fact]
    public void Decide_RejectsWhenTrainQualityP10NotImproved()
    {
        SuiteAggregate baselineTrain = Agg(1.0, 6.0);
        SuiteAggregate candTrain = Agg(1.0, 6.0); // equal p10, not strictly greater
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(1.0, 6.0);

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 2);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("Train quality p10 not improved");
    }

    [Fact]
    public void Decide_RejectsWhenHeldOutP10Regressed()
    {
        SuiteAggregate baselineTrain = Agg(1.0, 6.0);
        SuiteAggregate candTrain = Agg(1.0, 8.0);
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(1.0, 5.0); // held-out lower bound dropped

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 2);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("Held-out quality p10 regressed");
    }

    [Fact]
    public void Decide_RejectsWhenPairwiseNetNotPositive()
    {
        SuiteAggregate baselineTrain = Agg(1.0, 6.0);
        SuiteAggregate candTrain = Agg(1.0, 7.0);
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(1.0, 6.0);

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 0);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("Pairwise net not positive");
    }

    // ── Invariant-improvement claim (added after live campaign 1cdc9ea7, where three candidates fixed the
    //    only failing invariant with quality held and the gate rejected every one of them) ──

    [Fact]
    public void Decide_AcceptsInvariantImprovementWithQualityHeld_EvenWhenPairwiseTies()
    {
        // The exact live shape: baseline train inv 89%, candidate fixes it to 100%, p10 ties, pairwise -1.
        SuiteAggregate baselineTrain = Agg(0.889, 8.0);
        SuiteAggregate candTrain = Agg(1.0, 8.0);
        SuiteAggregate baselineHeldOut = Agg(1.0, 8.0);
        SuiteAggregate candHeldOut = Agg(1.0, 8.0);

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: -1);

        d.Accept.Should().BeTrue("a hard-gate fix with quality held must not be vetoed by a style-preference vote");
        d.Reason.Should().Contain("invariant pass rate");
    }

    [Fact]
    public void Decide_RejectsInvariantImprovementWhenQualityRegressed()
    {
        SuiteAggregate baselineTrain = Agg(0.889, 8.0);
        SuiteAggregate candTrain = Agg(1.0, 7.5); // fixed the invariant by paying quality — not acceptable
        SuiteAggregate baselineHeldOut = Agg(1.0, 8.0);
        SuiteAggregate candHeldOut = Agg(1.0, 8.0);

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 2);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("quality p10 regressed");
    }

    [Fact]
    public void Decide_InvariantClaimStillSubjectToHeldOutGate()
    {
        SuiteAggregate baselineTrain = Agg(0.889, 8.0);
        SuiteAggregate candTrain = Agg(1.0, 8.0);
        SuiteAggregate baselineHeldOut = Agg(1.0, 8.0);
        SuiteAggregate candHeldOut = Agg(0.9, 8.0); // the "fix" broke a held-out invariant — overfit

        GateDecision d = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: 0);

        d.Accept.Should().BeFalse();
        d.Reason.Should().Contain("Invariant regression on held-out");
    }

    [Fact]
    public void Decide_QualityClaimStillRequiresPositivePairwise()
    {
        // No invariant improvement (both 1.0): the quality claim keeps its pairwise veto.
        SuiteAggregate baselineTrain = Agg(1.0, 6.0);
        SuiteAggregate candTrain = Agg(1.0, 7.0);
        SuiteAggregate baselineHeldOut = Agg(1.0, 6.0);
        SuiteAggregate candHeldOut = Agg(1.0, 6.0);

        GatePolicy.DecidePairwise(-1, invariantImproved: false).Accept.Should().BeFalse();
        GatePolicy.DecidePairwise(-1, invariantImproved: true).Accept.Should().BeTrue();
        GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet: -1)
            .Accept.Should().BeFalse();
    }
}
