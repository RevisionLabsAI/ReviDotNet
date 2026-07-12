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
    /// Decides whether to accept a candidate variant. A candidate must claim ONE of two improvements on
    /// train — (a) quality: strict p10 improvement, or (b) invariants: strict pass-rate improvement with
    /// quality p10 not regressed — and must additionally satisfy invariant non-regression on both splits
    /// (no previously-passing invariant id drops), held-out quality p10 non-regression, and (for
    /// quality-claim candidates only) a strictly positive pairwise net on train. For invariant-claim
    /// candidates the pairwise result is advisory: a hard-gate fix with unchanged quality routinely ties or
    /// narrowly loses a preference vote, and vetoing on that starves the campaign of exactly the fixes it
    /// was launched for. Otherwise rejects naming the first failed condition.
    /// <para>
    /// Composed from the three staged checks below (<see cref="DecideTrain"/> →
    /// <see cref="DecidePairwise"/> → <see cref="DecideHeldOut"/>) so the campaign loop can FAIL FAST:
    /// train-side checks need only the (already-scored) train cards, the pairwise gate costs ≤8 LLM calls,
    /// and the held-out set (the most expensive evidence to gather) is scored last and only for candidates
    /// that survived everything cheaper.
    /// </para>
    /// </summary>
    public static GateDecision Decide(
        SuiteAggregate baselineTrain,
        SuiteAggregate candTrain,
        SuiteAggregate baselineHeldOut,
        SuiteAggregate candHeldOut,
        int pairwiseNet)
    {
        GateDecision train = DecideTrain(baselineTrain, candTrain);
        if (!train.Accept) return train;

        GateDecision pairwise = DecidePairwise(pairwiseNet, InvariantImproved(baselineTrain, candTrain));
        if (!pairwise.Accept) return pairwise;

        GateDecision heldOut = DecideHeldOut(baselineHeldOut, candHeldOut);
        if (!heldOut.Accept) return heldOut;

        return new GateDecision(true,
            DescribeAccept(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet));
    }

    /// <summary>True when the candidate strictly improves the train invariant pass-rate over baseline.</summary>
    public static bool InvariantImproved(SuiteAggregate baselineTrain, SuiteAggregate candTrain) =>
        candTrain.InvariantPassRate > baselineTrain.InvariantPassRate + EPS;

    /// <summary>
    /// The shared accept-reason text (single-sourced for <see cref="Decide"/> and the campaign loop),
    /// naming which claim carried the accept.
    /// </summary>
    public static string DescribeAccept(
        SuiteAggregate baselineTrain,
        SuiteAggregate candTrain,
        SuiteAggregate baselineHeldOut,
        SuiteAggregate candHeldOut,
        int pairwiseNet)
    {
        string claim = candTrain.QualityP10 > baselineTrain.QualityP10 + MARGIN
            ? $"train p10 {candTrain.QualityP10:F4} > {baselineTrain.QualityP10:F4}"
            : $"train invariant pass rate {candTrain.InvariantPassRate:F4} > {baselineTrain.InvariantPassRate:F4} (p10 held at {candTrain.QualityP10:F4})";
        return $"Accepted: {claim}, " +
               $"held-out p10 {candHeldOut.QualityP10:F4} >= {baselineHeldOut.QualityP10:F4}, " +
               $"invariants non-regressed, pairwise net {pairwiseNet}.";
    }

    /// <summary>
    /// Stage 1 — train-side checks only: invariant non-regression on train (aggregate + per-id), then one
    /// of the two improvement claims — strict quality p10 improvement, OR strict invariant pass-rate
    /// improvement with quality p10 not regressed. Requires no held-out scoring and no pairwise calls, so a
    /// candidate failing here costs nothing further.
    /// </summary>
    public static GateDecision DecideTrain(SuiteAggregate baselineTrain, SuiteAggregate candTrain)
    {
        if (candTrain.InvariantPassRate < baselineTrain.InvariantPassRate - EPS)
            return new GateDecision(false,
                $"Invariant regression on train: pass rate {candTrain.InvariantPassRate:F4} < baseline {baselineTrain.InvariantPassRate:F4}.");

        string? droppedTrain = FirstDroppedInvariant(baselineTrain.InvariantPassRateById, candTrain.InvariantPassRateById);
        if (droppedTrain is not null)
            return new GateDecision(false,
                $"Invariant regression on train: invariant '{droppedTrain}' dropped from passing.");

        if (candTrain.QualityP10 > baselineTrain.QualityP10 + MARGIN)
            return new GateDecision(true, "train checks passed (quality p10 improved)");

        if (InvariantImproved(baselineTrain, candTrain))
        {
            return candTrain.QualityP10 >= baselineTrain.QualityP10 - EPS
                ? new GateDecision(true, "train checks passed (invariant pass rate improved, quality p10 held)")
                : new GateDecision(false,
                    $"Invariant pass rate improved but train quality p10 regressed: {candTrain.QualityP10:F4} < baseline {baselineTrain.QualityP10:F4}.");
        }

        return new GateDecision(false,
            $"Train quality p10 not improved: {candTrain.QualityP10:F4} <= baseline {baselineTrain.QualityP10:F4} + margin {MARGIN:F4} (and invariant pass rate not improved).");
    }

    /// <summary>
    /// Stage 2 — the pairwise gate (≤8 LLM calls). For quality-claim candidates the net must be strictly
    /// positive: their whole claim is "the judge thinks these outputs are better", so a preference vote that
    /// disagrees is a veto. For invariant-claim candidates (<paramref name="invariantImproved"/> true) the
    /// vote is advisory only: the claim is a hard-gate fix with quality held, which a style-preference vote
    /// has no standing to veto.
    /// </summary>
    public static GateDecision DecidePairwise(int pairwiseNet, bool invariantImproved = false)
    {
        if (invariantImproved)
            return new GateDecision(true, $"pairwise advisory (net {pairwiseNet}) — invariant improvement carries the claim");
        return pairwiseNet <= 0
            ? new GateDecision(false, $"Pairwise net not positive on train: net {pairwiseNet} <= 0.")
            : new GateDecision(true, "pairwise net positive");
    }

    /// <summary>
    /// Stage 3 — held-out checks: invariant non-regression (aggregate + per-id) and quality p10
    /// non-regression on the held-out set. The most expensive evidence — gathered last.
    /// </summary>
    public static GateDecision DecideHeldOut(SuiteAggregate baselineHeldOut, SuiteAggregate candHeldOut)
    {
        if (candHeldOut.InvariantPassRate < baselineHeldOut.InvariantPassRate - EPS)
            return new GateDecision(false,
                $"Invariant regression on held-out: pass rate {candHeldOut.InvariantPassRate:F4} < baseline {baselineHeldOut.InvariantPassRate:F4}.");

        string? droppedHeldOut = FirstDroppedInvariant(baselineHeldOut.InvariantPassRateById, candHeldOut.InvariantPassRateById);
        if (droppedHeldOut is not null)
            return new GateDecision(false,
                $"Invariant regression on held-out: invariant '{droppedHeldOut}' dropped from passing.");

        if (candHeldOut.QualityP10 < baselineHeldOut.QualityP10 - EPS)
            return new GateDecision(false,
                $"Held-out quality p10 regressed: {candHeldOut.QualityP10:F4} < baseline {baselineHeldOut.QualityP10:F4}.");

        return new GateDecision(true, "held-out checks passed");
    }

    /// <summary>
    /// Screening gate for the cheap 1-sample pre-pass: rejects only CLEAR losers — candidates whose
    /// single-sample train screen falls well below the baseline on invariants or quality. Margins are
    /// deliberately loose (one sample is noisy); anything not clearly worse proceeds to the full evaluation.
    /// </summary>
    public static GateDecision DecideScreen(SuiteAggregate baselineTrain, SuiteAggregate screen)
    {
        const double InvariantSlack = 0.10;
        const double QualitySlack = 1.0;

        if (screen.InvariantPassRate < baselineTrain.InvariantPassRate - InvariantSlack - EPS)
            return new GateDecision(false,
                $"Screen (1-sample train): invariant pass rate {screen.InvariantPassRate:F4} is more than {InvariantSlack:F2} below baseline {baselineTrain.InvariantPassRate:F4}.");

        if (screen.QualityP10 < baselineTrain.QualityP10 - QualitySlack - EPS)
            return new GateDecision(false,
                $"Screen (1-sample train): quality p10 {screen.QualityP10:F4} is more than {QualitySlack:F1} below baseline {baselineTrain.QualityP10:F4}.");

        return new GateDecision(true, "screen passed");
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
