// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Aggregates a set of <see cref="ScoreCard"/>s (one agent variant over a suite) into a
/// <see cref="SuiteAggregate"/>. Uses lower-bound statistics (pass-rate, p10 quality) so acceptance
/// decisions don't reward a high mean that hides bad worst-cases.
/// </summary>
public static class Aggregator
{
    /// <summary>Roll up the score cards. Empty input yields an all-zero aggregate.</summary>
    public static SuiteAggregate Aggregate(IReadOnlyList<ScoreCard> cards)
    {
        if (cards.Count == 0)
            return new SuiteAggregate();

        // Compute the structural pass-rate over GATED runs only (runs that actually evaluated ≥1
        // invariant), so ungated runs can't inflate it to a false 100%. GatedRunCount surfaces the caveat.
        int gatedCount = cards.Count(c => c.Gated);
        double passRate = gatedCount > 0
            ? cards.Count(c => c.Gated && c.Verdict == RunVerdict.Pass) / (double)gatedCount
            : 1.0;

        List<double> qualities = cards
            .Where(c => c.Quality is not null)
            .Select(c => (double)c.Quality!.Overall)
            .OrderBy(x => x)
            .ToList();
        double qMean = qualities.Count > 0 ? qualities.Average() : 0;
        double qP10 = Percentile(qualities, 10);

        List<decimal> costs = cards.Where(c => c.Efficiency is not null).Select(c => c.Efficiency!.CostUsd).ToList();
        decimal costMean = costs.Count > 0 ? costs.Average() : 0m;

        List<double> latencies = cards
            .Where(c => c.Efficiency is not null)
            .Select(c => (double)c.Efficiency!.LatencyMs)
            .OrderBy(x => x)
            .ToList();
        long latP90 = (long)Percentile(latencies, 90);

        Dictionary<string, double> byId = cards
            .SelectMany(c => c.Invariants)
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.Count(i => i.Passed) / (double)g.Count());

        return new SuiteAggregate
        {
            InvariantPassRate = passRate,
            QualityMean = qMean,
            QualityP10 = qP10,
            CostMean = costMean,
            LatencyP90Ms = latP90,
            RunCount = cards.Count,
            GatedRunCount = gatedCount,
            InvariantPassRateById = byId
        };
    }

    /// <summary>Linear-interpolated percentile over a pre-sorted ascending list.</summary>
    internal static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        double frac = rank - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}
