// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>The acceptance decision for a candidate variant, with a human-readable reason.</summary>
public sealed record GateDecision(bool Accept, string Reason);

/// <summary>
/// The pure, deterministic acceptance gate for a candidate variant. The campaign loop and the unit tests
/// share this exact function so the policy is single-sourced. Every comparison uses LOWER BOUNDS
/// (pass-rate, p10), never means, to resist sampling nondeterminism.
/// </summary>
public static class GatePolicy
{
    /// <summary>Floating-point tolerance for non-regression comparisons (treat near-equal as equal).</summary>
    public const double EPS = 1e-9;

    /// <summary>Minimum strict improvement required on the train quality lower bound.</summary>
    public const double MARGIN = 0.0;

    /// <summary>
    /// Decides whether to accept a candidate variant. Accepts iff ALL of: (1) invariant non-regression on
    /// both train and held-out (and no previously-passing invariant id drops to failing), (2) train quality
    /// improves on the p10 lower bound, (3) held-out quality p10 is not regressed, and (4) pairwise net is
    /// strictly positive on train. Otherwise rejects naming the first failed condition.
    /// </summary>
    public static GateDecision Decide(
        SuiteAggregate baselineTrain,
        SuiteAggregate candTrain,
        SuiteAggregate baselineHeldOut,
        SuiteAggregate candHeldOut,
        int pairwiseNet)
    {
        // (1) Invariant non-regression — aggregate pass rate, train then held-out.
        if (candTrain.InvariantPassRate < baselineTrain.InvariantPassRate - EPS)
            return new GateDecision(false,
                $"Invariant regression on train: pass rate {candTrain.InvariantPassRate:F4} < baseline {baselineTrain.InvariantPassRate:F4}.");

        if (candHeldOut.InvariantPassRate < baselineHeldOut.InvariantPassRate - EPS)
            return new GateDecision(false,
                $"Invariant regression on held-out: pass rate {candHeldOut.InvariantPassRate:F4} < baseline {baselineHeldOut.InvariantPassRate:F4}.");

        // (1, continued) No previously-passing invariant id may drop to failing — check per-id where available.
        string? droppedTrain = FirstDroppedInvariant(baselineTrain.InvariantPassRateById, candTrain.InvariantPassRateById);
        if (droppedTrain is not null)
            return new GateDecision(false,
                $"Invariant regression on train: invariant '{droppedTrain}' dropped from passing.");

        string? droppedHeldOut = FirstDroppedInvariant(baselineHeldOut.InvariantPassRateById, candHeldOut.InvariantPassRateById);
        if (droppedHeldOut is not null)
            return new GateDecision(false,
                $"Invariant regression on held-out: invariant '{droppedHeldOut}' dropped from passing.");

        // (2) Train quality improves on the lower bound (p10).
        if (!(candTrain.QualityP10 > baselineTrain.QualityP10 + MARGIN))
            return new GateDecision(false,
                $"Train quality p10 not improved: {candTrain.QualityP10:F4} <= baseline {baselineTrain.QualityP10:F4} + margin {MARGIN:F4}.");

        // (3) Held-out quality lower bound not regressed.
        if (candHeldOut.QualityP10 < baselineHeldOut.QualityP10 - EPS)
            return new GateDecision(false,
                $"Held-out quality p10 regressed: {candHeldOut.QualityP10:F4} < baseline {baselineHeldOut.QualityP10:F4}.");

        // (4) Pairwise net-positive on train.
        if (pairwiseNet <= 0)
            return new GateDecision(false,
                $"Pairwise net not positive on train: net {pairwiseNet} <= 0.");

        return new GateDecision(true,
            $"Accepted: train p10 {candTrain.QualityP10:F4} > {baselineTrain.QualityP10:F4}, " +
            $"held-out p10 {candHeldOut.QualityP10:F4} >= {baselineHeldOut.QualityP10:F4}, " +
            $"invariants non-regressed, pairwise net {pairwiseNet} > 0.");
    }

    /// <summary>
    /// Returns the id of the first invariant that was passing in <paramref name="baseline"/> but drops to a
    /// strictly lower pass rate in <paramref name="candidate"/>, or null if none regressed. Ids absent from
    /// the candidate map are treated as fully regressed (dropped to 0).
    /// </summary>
    private static string? FirstDroppedInvariant(
        IReadOnlyDictionary<string, double>? baseline,
        IReadOnlyDictionary<string, double>? candidate)
    {
        if (baseline is null || baseline.Count == 0)
            return null;

        foreach ((string id, double baseRate) in baseline)
        {
            double candRate = candidate is not null && candidate.TryGetValue(id, out double r) ? r : 0.0;
            if (candRate < baseRate - EPS)
                return id;
        }
        return null;
    }
}
