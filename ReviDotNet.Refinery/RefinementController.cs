// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>A progress update emitted during a campaign.</summary>
/// <param name="Message">A human-readable status line.</param>
/// <param name="ScoreCards">Score cards completed so far this round (may be empty).</param>
public sealed record RefineryProgress(string Message, IReadOnlyList<ScoreCard>? ScoreCards = null);

/// <summary>
/// Orchestrates evaluation and (in later phases) refinement of an agent against a scenario suite.
/// <para>
/// Phase 0/3 implements <see cref="MeasureBaselineAsync"/> — run each scenario N times, score with the
/// structural invariants + LLM judge + efficiency metrics, and aggregate into a baseline. The full
/// propose → re-run → regression-gate → accept loop is Phase 4.
/// </para>
/// </summary>
public sealed class RefinementController(RefinementRunner runner, ILlmJudge judge, ICampaignStore store)
{
    private readonly RefinementRunner _runner = runner;
    private readonly ILlmJudge _judge = judge;
    private readonly ICampaignStore _store = store;

    /// <summary>
    /// Measure a baseline for an agent: run every scenario in <paramref name="suite"/>
    /// <see cref="CampaignSpec.SamplesPerScenario"/> times, score each run, and persist a campaign whose
    /// baseline aggregate is the result.
    /// </summary>
    public async Task<Campaign> MeasureBaselineAsync(
        CampaignSpec spec,
        ScenarioSuite suite,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        IProgress<RefineryProgress>? progress = null,
        CancellationToken ct = default)
    {
        string campaignId = Guid.NewGuid().ToString("n");
        Campaign campaign = new()
        {
            Id = campaignId,
            Spec = spec,
            Status = CampaignStatus.Running
        };
        await _store.SaveAsync(campaign, ct);

        List<ScoreCard> cards = [];
        int total = suite.Scenarios.Count * Math.Max(1, spec.SamplesPerScenario);
        int done = 0;

        try
        {
            foreach (Scenario scenario in suite.Scenarios)
            {
                for (int sample = 0; sample < Math.Max(1, spec.SamplesPerScenario); sample++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new RefineryProgress($"[{++done}/{total}] {scenario.Id} sample {sample + 1}"));

                    ScoreCard card = await ScoreOnceAsync(scenario, agentDefinition, checkers, spec.Mode, sample, ct);
                    cards.Add(card);
                }
            }

            SuiteAggregate aggregate = Aggregator.Aggregate(cards);
            campaign = campaign with
            {
                Status = CampaignStatus.Converged,
                Baseline = aggregate,
                Current = aggregate
            };
            await _store.SaveAsync(campaign, ct);
            progress?.Report(new RefineryProgress(
                $"baseline: invariant pass-rate {aggregate.InvariantPassRate:P0}, quality mean {aggregate.QualityMean:F1} (p10 {aggregate.QualityP10:F1}) over {aggregate.RunCount} runs",
                cards));
            return campaign;
        }
        catch (OperationCanceledException)
        {
            campaign = campaign with { Status = CampaignStatus.Stopped };
            await _store.SaveAsync(campaign, ct: default);
            throw;
        }
        catch (Exception ex)
        {
            campaign = campaign with { Status = CampaignStatus.Failed, Error = ex.Message };
            await _store.SaveAsync(campaign, ct: default);
            throw;
        }
    }

    /// <summary>Run one scenario sample and produce its full <see cref="ScoreCard"/>.</summary>
    public async Task<ScoreCard> ScoreOnceAsync(
        Scenario scenario,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int sampleIndex,
        CancellationToken ct = default)
    {
        AgentRun run = await _runner.RunOnceAsync(scenario.AgentName, scenario.Inputs, ct);
        IReadOnlyList<InvariantResult> invariants = StructuralScorer.Score(run.Trace, scenario, checkers);
        QualityScore? quality = scenario.Rubric.Count > 0
            ? await _judge.JudgeAsync(run.Trace, agentDefinition, scenario, invariants, ct)
            : null;
        EfficiencyMetrics efficiency = EfficiencyExtractor.Extract(run.Trace, run.LatencyMs);
        return ScoreCardBuilder.Build(scenario, run.Trace, invariants, quality, efficiency, sampleIndex, mode);
    }
}
