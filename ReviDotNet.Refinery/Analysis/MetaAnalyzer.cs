// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Effectiveness of one knob class for one agent, aggregated across every campaign in the store.
/// </summary>
/// <param name="AgentName">The agent the knob was applied to.</param>
/// <param name="KnobType">The knob class (system-prompt, sampling, few-shot, …).</param>
/// <param name="Attempts">How many ledger entries attempted this knob class.</param>
/// <param name="Accepted">How many of those attempts were accepted.</param>
/// <param name="AcceptanceRate">Accepted / Attempts (0 when there were no attempts).</param>
/// <param name="AcceptedMeanQualityP10">
/// Mean quality-P10 over the accepted attempts, preferring each entry's train score and falling back to its
/// held-out score when train is absent (0 when none were accepted).
/// </param>
public record KnobEffectiveness(
    string AgentName,
    string KnobType,
    int Attempts,
    int Accepted,
    double AcceptanceRate,
    double AcceptedMeanQualityP10);

/// <summary>
/// Mines the experiment ledger to learn which knob classes actually move the needle, per agent. This is the
/// meta layer over individual campaigns: it answers "across everything we've ever tried, which kinds of
/// changes get accepted, and how good are the accepted ones?" so the proposer/operator can bias toward the
/// knobs that pay off.
/// </summary>
public sealed class MetaAnalyzer(ICampaignStore store)
{
    /// <summary>
    /// Aggregate knob effectiveness across all campaigns, optionally restricted to a single agent.
    /// </summary>
    /// <param name="agentName">When non-null, only entries for this agent (case-insensitive) are included.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One row per (agent, knob-type), ordered by acceptance rate desc, then attempts desc.</returns>
    public async Task<IReadOnlyList<KnobEffectiveness>> AnalyzeAsync(
        string? agentName = null, CancellationToken ct = default)
    {
        IReadOnlyList<Campaign> campaigns = await store.ListAsync(ct).ConfigureAwait(false);

        List<LedgerEntry> entries = [];
        foreach (Campaign campaign in campaigns)
        {
            ct.ThrowIfCancellationRequested();
            entries.AddRange(await store.GetLedgerAsync(campaign.Id, ct).ConfigureAwait(false));
        }

        IEnumerable<LedgerEntry> filtered = agentName is null
            ? entries
            : entries.Where(e => string.Equals(e.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

        return filtered
            .GroupBy(e => (e.AgentName, e.KnobType))
            .Select(g =>
            {
                int attempts = g.Count();
                List<LedgerEntry> accepted = g.Where(e => e.Accepted).ToList();
                double acceptanceRate = attempts == 0 ? 0 : (double)accepted.Count / attempts;
                double meanP10 = accepted.Count == 0
                    ? 0
                    : accepted.Average(e => e.TrainScores?.QualityP10 ?? e.HeldOutScores?.QualityP10 ?? 0);

                return new KnobEffectiveness(
                    g.Key.AgentName,
                    g.Key.KnobType,
                    attempts,
                    accepted.Count,
                    acceptanceRate,
                    meanP10);
            })
            .OrderByDescending(k => k.AcceptanceRate)
            .ThenByDescending(k => k.Attempts)
            .ToList();
    }
}
