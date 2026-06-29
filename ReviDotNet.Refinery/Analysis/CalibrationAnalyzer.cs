// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Accuracy of one confidence level for a fact-checker agent: how many runs reported this confidence, how
/// many of those were actually correct, the resulting accuracy, and the weighted contribution to calibration
/// error.
/// </summary>
/// <param name="ConfidenceLevel">The self-reported confidence bucket (1–5).</param>
/// <param name="RunCount">Runs that reported this confidence and had ground truth.</param>
/// <param name="CorrectCount">How many of those runs picked the correct winner.</param>
/// <param name="Accuracy">CorrectCount / RunCount (0 when there were no runs in the bucket).</param>
/// <param name="WeightedError">
/// <c>(RunCount / total) * |Accuracy - (ConfidenceLevel - 0.5) / 5|</c> — this bucket's contribution to ECE.
/// </param>
public record ConfidenceBucket(
    int ConfidenceLevel,
    int RunCount,
    int CorrectCount,
    double Accuracy,
    double WeightedError);

/// <summary>
/// Calibration report for a fact-checker agent: per-confidence accuracy, the Expected Calibration Error, and
/// whether accuracy rises monotonically with stated confidence.
/// </summary>
/// <param name="AgentName">The agent analysed.</param>
/// <param name="AgentVersion">The variant analysed, when restricted to one (else null).</param>
/// <param name="TotalRuns">Runs with a determination AND a ground truth to compare against.</param>
/// <param name="CalibratedRuns">Of those, how many picked the correct winner.</param>
/// <param name="Buckets">One row per non-empty confidence level (1–5), ascending by confidence.</param>
/// <param name="Ece">Expected Calibration Error: Σ (n_b/N)·|acc_b − (b−0.5)/5| over non-empty buckets.</param>
/// <param name="MonotonicAccuracy">
/// True when accuracy is non-decreasing across the (ascending) non-empty buckets — i.e. higher stated
/// confidence never scores worse than a lower one. Vacuously true with fewer than two non-empty buckets.
/// </param>
public record CalibrationReport(
    string AgentName,
    string? AgentVersion,
    int TotalRuns,
    int CalibratedRuns,
    IReadOnlyList<ConfidenceBucket> Buckets,
    double Ece,
    bool MonotonicAccuracy);

/// <summary>
/// Optional capability a <see cref="ICampaignStore"/> MAY also implement to expose the per-run
/// <see cref="ScoreCard"/>s it captured. The base store contract persists only campaigns and the ledger;
/// calibration needs the individual cards (with their <see cref="FactCheckerDetermination"/>), so the
/// analyzer detects this at runtime via an <c>is</c> check — stores that don't keep cards simply do not
/// implement it, and the store-based overload then yields an empty report.
/// </summary>
public interface IScoreCardSource
{
    /// <summary>Every captured score card across all campaigns.</summary>
    Task<IReadOnlyList<ScoreCard>> GetScoreCardsAsync(CancellationToken ct = default);

    /// <summary>Ground truth for a scenario id, when known (e.g. the expected fact-checker winner).</summary>
    Task<IReadOnlyDictionary<string, string>> GetGroundTruthAsync(CancellationToken ct = default);
}

/// <summary>
/// Measures how well a fact-checker agent's <i>stated confidence</i> matches its <i>actual accuracy</i>. For
/// each run it joins the run's <see cref="FactCheckerDetermination"/> to the scenario's ground truth, then
/// computes per-confidence-bucket accuracy, the Expected Calibration Error, and whether accuracy increases
/// monotonically with confidence. A well-calibrated fact-checker is right ~(b−0.5)/5 of the time when it
/// reports confidence <c>b</c> on the 1–5 scale.
/// </summary>
public sealed class CalibrationAnalyzer(ICampaignStore store)
{
    /// <summary>
    /// Analyse calibration using score cards drawn from the store (requires the store to implement
    /// <see cref="IScoreCardSource"/>; otherwise an empty report is returned).
    /// </summary>
    public async Task<CalibrationReport> AnalyzeAsync(
        string agentName, string? agentVersion = null, CancellationToken ct = default)
    {
        if (store is not IScoreCardSource source)
            return Empty(agentName, agentVersion);

        IReadOnlyList<ScoreCard> cards = await source.GetScoreCardsAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> groundTruth = await source.GetGroundTruthAsync(ct).ConfigureAwait(false);

        return Analyze(agentName, agentVersion, cards, groundTruth);
    }

    /// <summary>
    /// Analyse calibration where the caller supplies the scenario-id → ground-truth map directly (e.g. from a
    /// scenario suite the store does not hold). Score cards are still drawn from the store.
    /// </summary>
    public async Task<CalibrationReport> AnalyzeAsync(
        string agentName,
        IReadOnlyDictionary<string, string> groundTruthByScenarioId,
        string? agentVersion = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<ScoreCard> cards = store is IScoreCardSource source
            ? await source.GetScoreCardsAsync(ct).ConfigureAwait(false)
            : [];

        return Analyze(agentName, agentVersion, cards, groundTruthByScenarioId);
    }

    private static CalibrationReport Empty(string agentName, string? agentVersion) =>
        new(agentName, agentVersion, 0, 0, [], 0, true);

    /// <summary>Core calibration math over a set of cards and a ground-truth map. Pure; no I/O.</summary>
    private static CalibrationReport Analyze(
        string agentName,
        string? agentVersion,
        IReadOnlyList<ScoreCard> cards,
        IReadOnlyDictionary<string, string> groundTruth)
    {
        // Keep only this agent's (optionally this version's) runs that have BOTH a determination and a
        // ground truth to compare against.
        List<(int Confidence, bool Correct)> runs = [];
        foreach (ScoreCard card in cards)
        {
            if (!string.Equals(card.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (agentVersion is not null &&
                !string.Equals(card.AgentVersion, agentVersion, StringComparison.OrdinalIgnoreCase))
                continue;
            if (card.FactCheckerDetermination is not { } det)
                continue;
            if (!groundTruth.TryGetValue(card.ScenarioId, out string? truth) || string.IsNullOrEmpty(truth))
                continue;

            bool correct = string.Equals(det.Winner, truth, StringComparison.OrdinalIgnoreCase);
            runs.Add((det.Confidence, correct));
        }

        int total = runs.Count;
        if (total == 0)
            return Empty(agentName, agentVersion);

        int calibrated = runs.Count(r => r.Correct);

        // One bucket per non-empty confidence level (1–5), ascending.
        List<ConfidenceBucket> buckets = [];
        for (int level = 1; level <= 5; level++)
        {
            List<(int Confidence, bool Correct)> inBucket = runs.Where(r => r.Confidence == level).ToList();
            if (inBucket.Count == 0)
                continue;

            int correctCount = inBucket.Count(r => r.Correct);
            double accuracy = (double)correctCount / inBucket.Count;
            double expected = (level - 0.5) / 5.0;
            double weightedError = ((double)inBucket.Count / total) * Math.Abs(accuracy - expected);

            buckets.Add(new ConfidenceBucket(level, inBucket.Count, correctCount, accuracy, weightedError));
        }

        double ece = buckets.Sum(b => b.WeightedError);

        // Monotonic when accuracy never decreases as confidence rises across the populated buckets.
        bool monotonic = true;
        for (int i = 1; i < buckets.Count; i++)
        {
            if (buckets[i].Accuracy < buckets[i - 1].Accuracy)
            {
                monotonic = false;
                break;
            }
        }

        return new CalibrationReport(agentName, agentVersion, total, calibrated, buckets, ece, monotonic);
    }
}
