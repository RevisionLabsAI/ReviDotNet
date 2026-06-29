// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;

namespace Revi.Refinery;

/// <summary>A progress update emitted during a campaign.</summary>
/// <param name="Message">A human-readable status line.</param>
/// <param name="ScoreCards">Score cards completed so far this round (may be empty).</param>
public sealed record RefineryProgress(string Message, IReadOnlyList<ScoreCard>? ScoreCards = null);

/// <summary>
/// Orchestrates evaluation and refinement of an agent against a scenario suite.
/// <para>
/// <see cref="MeasureBaselineAsync"/> (Phase 0/3) runs each scenario N times, scores with the structural
/// invariants + LLM judge + efficiency metrics, and aggregates into a baseline.
/// </para>
/// <para>
/// <see cref="RunCampaignAsync"/> (Phase 4) closes the loop: measure baseline → propose a revision →
/// validate it → register it under a temp name → re-run on train + held-out → regression-gate
/// (<see cref="GatePolicy"/> + <see cref="PairwiseGate"/>) → accept/reject → iterate until convergence,
/// budget exhaustion, or the round cap.
/// </para>
/// </summary>
public sealed class RefinementController(
    RefinementRunner runner,
    ILlmJudge judge,
    ICampaignStore store,
    IProposalStrategy proposer,
    PairwiseGate pairwise,
    CandidateValidator validator,
    IAgentManager agents)
{
    private readonly RefinementRunner _runner = runner;
    private readonly ILlmJudge _judge = judge;
    private readonly ICampaignStore _store = store;
    private readonly IProposalStrategy _proposer = proposer;
    private readonly PairwiseGate _pairwise = pairwise;
    private readonly CandidateValidator _validator = validator;
    private readonly IAgentManager _agents = agents;

    /// <summary>Cap on the number of train scenarios sent to the (LLM-backed) pairwise judge per round.</summary>
    private const int MaxPairwiseScenarios = 8;

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
        CancellationToken ct = default,
        string? campaignId = null)
    {
        string id = campaignId ?? Guid.NewGuid().ToString("n");
        Campaign campaign = new()
        {
            Id = id,
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

    /// <summary>
    /// Run the full propose → validate → re-run → regression-gate → accept/reject loop for an agent.
    /// <para>
    /// Splits the suite into a train set (<c>!HeldOut</c>) used for proposal + the primary gate, and a
    /// held-out set used only for validation. If the suite has no held-out scenarios, held-out metrics are
    /// taken to equal the train metrics (so the held-out non-regression checks degenerate to train checks).
    /// </para>
    /// <para>
    /// <b>Budget accounting (known limitation):</b> the unit is agent-execution TOKENS
    /// (<see cref="AgentTrace.InputTokens"/> + <see cref="AgentTrace.OutputTokens"/> per run). Because
    /// <c>IInferService.ToObject&lt;T&gt;</c> discards the underlying <c>CompletionResult</c>, the token
    /// usage of the meta-LLMs (judge / pairwise / proposer) is NOT observable and is therefore NOT counted
    /// against the budget. Exact meta-LLM accounting via <c>IInferService.Completion</c> is a future
    /// enhancement.
    /// </para>
    /// </summary>
    public async Task<Campaign> RunCampaignAsync(
        CampaignSpec spec,
        ScenarioSuite suite,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        IProgress<RefineryProgress>? progress = null,
        CancellationToken ct = default,
        string? campaignId = null)
    {
        string id = campaignId ?? Guid.NewGuid().ToString("n");

        // (a) Split scenarios. An empty held-out set means held-out metrics mirror train metrics.
        List<Scenario> train = suite.Scenarios.Where(s => !s.HeldOut).ToList();
        List<Scenario> heldOut = suite.Scenarios.Where(s => s.HeldOut).ToList();
        bool heldOutMirrorsTrain = heldOut.Count == 0;

        // One reusable registry slot per campaign (there is no IAgentManager.Remove).
        string tempName = "__refinery/" + id + "/candidate";

        Campaign campaign = new()
        {
            Id = id,
            Spec = spec,
            Status = CampaignStatus.Running
        };
        await _store.SaveAsync(campaign, ct);

        BudgetGovernor governor = new(spec.TokenBudget);
        int samples = Math.Max(1, spec.SamplesPerScenario);

        try
        {
            // (b) Baseline: score train + held-out once each, keeping per-scenario train OUTPUT text for pairwise.
            string currentDefinition = agentDefinition;

            (List<ScoreCard> baselineTrainCards, Dictionary<string, string> baselineTrainOutputs) =
                await ScoreSetAsync(train, spec.AgentName, currentDefinition, checkers, spec.Mode, samples, governor, ct);

            List<ScoreCard> baselineHeldOutCards = heldOutMirrorsTrain
                ? baselineTrainCards
                : (await ScoreSetAsync(heldOut, spec.AgentName, currentDefinition, checkers, spec.Mode, samples, governor, ct)).Cards;

            SuiteAggregate baselineTrain = Aggregator.Aggregate(baselineTrainCards);
            SuiteAggregate baselineHeldOut = Aggregator.Aggregate(baselineHeldOutCards);

            campaign = campaign with { Baseline = baselineTrain, Current = baselineTrain, TokensSpent = governor.Spent };
            await _store.SaveAsync(campaign, ct);
            progress?.Report(new RefineryProgress(
                $"baseline: invariant pass-rate {baselineTrain.InvariantPassRate:P0}, quality p10 {baselineTrain.QualityP10:F1} over {baselineTrain.RunCount} train runs",
                baselineTrainCards));

            List<ScoreCard> currentTrainCards = baselineTrainCards;
            int noImprove = 0;

            // (c) Improvement rounds.
            for (int round = 1; round <= spec.MaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                // Budget gate at the top of the round.
                if (governor.Exhausted)
                {
                    campaign = campaign with { Status = CampaignStatus.BudgetExhausted, TokensSpent = governor.Spent };
                    await _store.SaveAsync(campaign, ct);
                    progress?.Report(new RefineryProgress($"round {round}: token budget exhausted ({governor.Spent} tokens)"));
                    return campaign;
                }

                progress?.Report(new RefineryProgress($"round {round}: proposing…"));
                Proposal? proposal = await _proposer.ProposeAsync(spec.AgentName, currentDefinition, baselineTrain, currentTrainCards, ct);

                if (proposal is null)
                {
                    // No useful change found this round. Record the round as an iteration with no variants so
                    // the campaign ledger reflects every round the loop executed (none accepted).
                    noImprove++;
                    campaign = campaign with
                    {
                        Iterations =
                        [
                            .. campaign.Iterations,
                            new CampaignIteration
                            {
                                Round = round,
                                Baseline = baselineTrain,
                                Variants = [],
                                AcceptedVariantId = null
                            }
                        ],
                        TokensSpent = governor.Spent
                    };
                    await _store.SaveAsync(campaign, ct);
                    progress?.Report(new RefineryProgress($"round {round}: no proposal (no-improvement {noImprove}/{spec.StopAfterNoImprovementRounds})"));
                    if (noImprove >= spec.StopAfterNoImprovementRounds) break;
                    continue;
                }

                // (c) Validate the candidate BEFORE spending any run budget on it.
                ValidationResult validation = _validator.Validate(proposal.RevisedContent, tempName);
                if (!validation.Ok)
                {
                    string errors = validation.Errors.Count > 0 ? string.Join("; ", validation.Errors) : "candidate failed validation";
                    await RecordRejectedAsync(campaign, id, round, spec.AgentName, proposal, currentTrainAgg: baselineTrain,
                        candTrain: null, candHeldOut: null, reason: errors, tokensSpent: governor.Spent, ct);
                    campaign = await ReloadAsync(id, campaign, ct);
                    noImprove++;
                    progress?.Report(new RefineryProgress($"round {round}: candidate rejected by validator — {errors}"));
                    if (noImprove >= spec.StopAfterNoImprovementRounds) break;
                    continue;
                }

                // (d) Register the candidate per the variant recipe and score it on train + held-out.
                RegisterCandidate(proposal.RevisedContent, tempName);

                (List<ScoreCard> candTrainCards, Dictionary<string, string> candTrainOutputs) =
                    await ScoreSetAsync(train, tempName, proposal.RevisedContent, checkers, spec.Mode, samples, governor, ct);

                List<ScoreCard> candHeldOutCards = heldOutMirrorsTrain
                    ? candTrainCards
                    : (await ScoreSetAsync(heldOut, tempName, proposal.RevisedContent, checkers, spec.Mode, samples, governor, ct)).Cards;

                SuiteAggregate candTrain = Aggregator.Aggregate(candTrainCards);
                SuiteAggregate candHeldOut = Aggregator.Aggregate(candHeldOutCards);

                // (e) Pairwise on (a capped sample of) train scenarios.
                int pairwiseNet = await PairwiseNetAsync(train, baselineTrainOutputs, candTrainOutputs, ct);

                // (f) Decide.
                GateDecision decision = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet);

                VariantRecord variant = new()
                {
                    Id = Guid.NewGuid().ToString("n"),
                    AgentName = spec.AgentName,
                    Round = round,
                    KnobType = proposal.KnobType,
                    Diff = proposal.Diff,
                    RevisedContent = proposal.RevisedContent,
                    TrainScores = candTrain,
                    HeldOutScores = candHeldOut,
                    Accepted = decision.Accept,
                    Decision = decision.Reason
                };

                await _store.AppendLedgerAsync(new LedgerEntry
                {
                    CampaignId = id,
                    Round = round,
                    AgentName = spec.AgentName,
                    KnobType = proposal.KnobType,
                    Diff = proposal.Diff,
                    TrainScores = candTrain,
                    HeldOutScores = candHeldOut,
                    Accepted = decision.Accept,
                    RejectReason = decision.Accept ? null : decision.Reason,
                    TokensSpent = governor.Spent
                }, ct);

                campaign = campaign with
                {
                    Iterations =
                    [
                        .. campaign.Iterations,
                        new CampaignIteration
                        {
                            Round = round,
                            Baseline = baselineTrain,
                            Variants = [variant],
                            AcceptedVariantId = decision.Accept ? variant.Id : null
                        }
                    ],
                    TokensSpent = governor.Spent
                };

                // (g) Adopt or reject.
                if (decision.Accept)
                {
                    currentDefinition = proposal.RevisedContent;
                    baselineTrain = candTrain;
                    baselineHeldOut = candHeldOut;
                    currentTrainCards = candTrainCards;
                    baselineTrainOutputs = candTrainOutputs;
                    campaign = campaign with { Current = candTrain };
                    noImprove = 0;
                    progress?.Report(new RefineryProgress($"round {round}: ACCEPTED ({proposal.KnobType}) — {decision.Reason}", candTrainCards));
                }
                else
                {
                    noImprove++;
                    progress?.Report(new RefineryProgress($"round {round}: rejected ({proposal.KnobType}) — {decision.Reason} (no-improvement {noImprove}/{spec.StopAfterNoImprovementRounds})", candTrainCards));
                }

                await _store.SaveAsync(campaign, ct);

                // Budget gate after evaluating the variant.
                if (governor.Exhausted)
                {
                    campaign = campaign with { Status = CampaignStatus.BudgetExhausted, TokensSpent = governor.Spent };
                    await _store.SaveAsync(campaign, ct);
                    progress?.Report(new RefineryProgress($"round {round}: token budget exhausted ({governor.Spent} tokens)"));
                    return campaign;
                }

                if (noImprove >= spec.StopAfterNoImprovementRounds)
                    break;
            }

            // (h) Terminal status — Converged unless an earlier branch already set a terminal state.
            campaign = campaign with { Status = CampaignStatus.Converged, TokensSpent = governor.Spent };
            await _store.SaveAsync(campaign, ct);
            progress?.Report(new RefineryProgress(
                $"campaign converged: current quality p10 {baselineTrain.QualityP10:F1}, {governor.Spent} tokens spent"));
            return campaign;
        }
        catch (OperationCanceledException)
        {
            campaign = campaign with { Status = CampaignStatus.Stopped, TokensSpent = governor.Spent };
            await _store.SaveAsync(campaign, ct: default);
            throw;
        }
        catch (Exception ex)
        {
            campaign = campaign with { Status = CampaignStatus.Failed, Error = ex.Message, TokensSpent = governor.Spent };
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

    /// <summary>
    /// Like <see cref="ScoreOnceAsync"/> but runs an EXPLICIT agent name (so a candidate registered under a
    /// temp name can be scored) and ALSO returns the run's final output text (needed for the pairwise gate,
    /// which <see cref="ScoreCard"/> does not carry). The given <paramref name="agentDefinition"/> is the
    /// text shown to the LLM judge.
    /// </summary>
    private async Task<(ScoreCard Card, string Output)> RunAndScoreAsync(
        Scenario scenario,
        string agentName,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int sampleIndex,
        CancellationToken ct)
    {
        AgentRun run = await _runner.RunOnceAsync(agentName, scenario.Inputs, ct);
        IReadOnlyList<InvariantResult> invariants = StructuralScorer.Score(run.Trace, scenario, checkers);
        QualityScore? quality = scenario.Rubric.Count > 0
            ? await _judge.JudgeAsync(run.Trace, agentDefinition, scenario, invariants, ct)
            : null;
        EfficiencyMetrics efficiency = EfficiencyExtractor.Extract(run.Trace, run.LatencyMs);
        ScoreCard card = ScoreCardBuilder.Build(scenario, run.Trace, invariants, quality, efficiency, sampleIndex, mode);
        return (card, run.Trace.FinalOutput ?? "");
    }

    /// <summary>
    /// Score a whole scenario set (every scenario × <paramref name="samples"/>), charging
    /// agent-execution tokens to <paramref name="governor"/>. Returns the score cards plus the LAST sample's
    /// output text per scenario id (a stable per-scenario representative for pairwise comparison).
    /// </summary>
    private async Task<(List<ScoreCard> Cards, Dictionary<string, string> Outputs)> ScoreSetAsync(
        IReadOnlyList<Scenario> scenarios,
        string agentName,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int samples,
        BudgetGovernor governor,
        CancellationToken ct)
    {
        List<ScoreCard> cards = [];
        Dictionary<string, string> outputs = [];

        foreach (Scenario scenario in scenarios)
        {
            for (int sample = 0; sample < samples; sample++)
            {
                ct.ThrowIfCancellationRequested();
                (ScoreCard card, string output) =
                    await RunAndScoreAsync(scenario, agentName, agentDefinition, checkers, mode, sample, ct);
                cards.Add(card);
                outputs[scenario.Id] = output; // last sample wins — a representative output for pairwise
                governor.Record(card.Efficiency is { } e ? e.InputTokens + e.OutputTokens : 0);
            }
        }

        return (cards, outputs);
    }

    /// <summary>
    /// Compare baseline vs candidate outputs over a capped sample of train scenarios and return the net
    /// candidate advantage: (#candidate wins) − (#baseline wins); ties are ignored. Scenarios missing an
    /// output on either side are skipped.
    /// </summary>
    private async Task<int> PairwiseNetAsync(
        IReadOnlyList<Scenario> train,
        IReadOnlyDictionary<string, string> baselineOutputs,
        IReadOnlyDictionary<string, string> candidateOutputs,
        CancellationToken ct)
    {
        int net = 0;
        int compared = 0;
        foreach (Scenario scenario in train)
        {
            if (compared >= MaxPairwiseScenarios) break;
            if (!baselineOutputs.TryGetValue(scenario.Id, out string? baseOut) ||
                !candidateOutputs.TryGetValue(scenario.Id, out string? candOut))
                continue;

            compared++;
            PairwiseVerdict? verdict = await _pairwise.CompareAsync(
                RenderScenarioText(scenario), RenderRubric(scenario), baseOut, candOut, ct);
            if (verdict is null) continue;

            if (verdict.Winner == PairwiseVerdict.CandidateWins) net++;
            else if (verdict.Winner == PairwiseVerdict.BaselineWins) net--;
        }
        return net;
    }

    /// <summary>Register a proposed revised .agent SOURCE under a temp name (the variant-execution recipe).</summary>
    private void RegisterCandidate(string revisedSource, string tempName)
    {
        Dictionary<string, string> data = RConfigParser.ReadEmbedded(revisedSource);
        AgentProfile candidate = AgentProfile.ToObject(data, namePrefix: "");
        candidate.Name = tempName;          // registration key; the internal graph does not reference the name
        _agents.AddOrReplace(candidate);    // reuses the single temp slot — there is no Remove
    }

    /// <summary>Append a rejected-variant ledger entry + iteration (used for validation failures, before any run).</summary>
    private async Task RecordRejectedAsync(
        Campaign campaign,
        string campaignId,
        int round,
        string agentName,
        Proposal proposal,
        SuiteAggregate currentTrainAgg,
        SuiteAggregate? candTrain,
        SuiteAggregate? candHeldOut,
        string reason,
        long tokensSpent,
        CancellationToken ct)
    {
        VariantRecord variant = new()
        {
            Id = Guid.NewGuid().ToString("n"),
            AgentName = agentName,
            Round = round,
            KnobType = proposal.KnobType,
            Diff = proposal.Diff,
            RevisedContent = proposal.RevisedContent,
            TrainScores = candTrain,
            HeldOutScores = candHeldOut,
            Accepted = false,
            Decision = reason
        };

        await _store.AppendLedgerAsync(new LedgerEntry
        {
            CampaignId = campaignId,
            Round = round,
            AgentName = agentName,
            KnobType = proposal.KnobType,
            Diff = proposal.Diff,
            TrainScores = candTrain,
            HeldOutScores = candHeldOut,
            Accepted = false,
            RejectReason = reason,
            TokensSpent = tokensSpent
        }, ct);

        Campaign updated = campaign with
        {
            Iterations =
            [
                .. campaign.Iterations,
                new CampaignIteration
                {
                    Round = round,
                    Baseline = currentTrainAgg,
                    Variants = [variant],
                    AcceptedVariantId = null
                }
            ],
            TokensSpent = tokensSpent
        };
        await _store.SaveAsync(updated, ct);
    }

    /// <summary>Re-read the campaign from the store so in-memory state matches what was just persisted.</summary>
    private async Task<Campaign> ReloadAsync(string id, Campaign fallback, CancellationToken ct) =>
        await _store.GetAsync(id, ct) ?? fallback;

    /// <summary>Render a scenario's inputs as the "Scenario" text for the pairwise judge.</summary>
    private static string RenderScenarioText(Scenario scenario)
    {
        StringBuilder sb = new();
        sb.Append("Scenario ").Append(scenario.Id);
        if (!string.IsNullOrWhiteSpace(scenario.Notes))
            sb.Append(" — ").Append(scenario.Notes);
        sb.Append('\n');
        foreach ((string key, string value) in scenario.Inputs)
            sb.Append(key).Append(": ").Append(value).Append('\n');
        return sb.ToString();
    }

    /// <summary>Render a scenario's rubric facet names as the "Rubric" text for the pairwise judge.</summary>
    private static string RenderRubric(Scenario scenario) =>
        scenario.Rubric.Count > 0 ? string.Join(", ", scenario.Rubric) : "(no rubric)";
}
