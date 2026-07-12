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
/// validate it → run the parsed candidate profile directly (per-run isolation, no shared registry slot) →
/// re-run on train + held-out → regression-gate
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
    IAgentManager agents,
    MetaLlmUsageBroker metaUsage)
{
    private readonly RefinementRunner _runner = runner;
    private readonly ILlmJudge _judge = judge;
    private readonly ICampaignStore _store = store;
    private readonly IProposalStrategy _proposer = proposer;
    private readonly PairwiseGate _pairwise = pairwise;
    private readonly CandidateValidator _validator = validator;
    private readonly IAgentManager _agents = agents;
    private readonly MetaLlmUsageBroker _metaUsage = metaUsage;

    /// <summary>Cap on the number of train scenarios sent to the (LLM-backed) pairwise judge per round.</summary>
    private const int MaxPairwiseScenarios = 8;

    /// <summary>
    /// Hard ceiling on candidates evaluated per round (the LLM proposal + every typed mutator). Defends the
    /// per-round cost even if the mutator registry grows; with the current registry the natural bound is
    /// already well under this.
    /// </summary>
    private const int MaxCandidatesPerRound = 16;

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
        string? campaignId = null,
        Func<Scenario, CancellationToken, Task>? seedScenario = null,
        IToolManager? toolManager = null)
    {
        string id = campaignId ?? Guid.NewGuid().ToString("n");
        Campaign campaign = new()
        {
            Id = id,
            Spec = spec,
            Status = CampaignStatus.Running
        };
        await _store.SaveAsync(campaign, ct);
        await PersistGroundTruthAsync(suite, ct);

        List<ScoreCard> cards = [];
        int total = suite.Scenarios.Count * Math.Max(1, spec.SamplesPerScenario);
        int done = 0;

        // Baseline-only campaigns spend real judge tokens too: open a meta scope so that spend is
        // visible on the campaign, and roll up agent tokens per run so mid-baseline status shows the
        // live meter instead of 0 until the end.
        using MetaLlmUsageBroker.Scope metaScope = _metaUsage.BeginScope();
        long agentTokens = 0;

        try
        {
            // Same parallel shape as ScoreSetCoreAsync: flatten the grid, run with bounded concurrency
            // (sequential when a seeding hook exists), reassemble deterministically. Progress and the
            // per-run campaign save mutate shared state, so they run under a lock.
            int maxParallel = seedScenario is null ? Math.Max(1, spec.MaxParallelRuns) : 1;
            List<(Scenario Scenario, int Sample, int Index)> work = [];
            int nextIndex = 0;
            foreach (Scenario scenario in suite.Scenarios)
                for (int sample = 0; sample < Math.Max(1, spec.SamplesPerScenario); sample++)
                    work.Add((scenario, sample, nextIndex++));

            var results = new ScoreCard[work.Count];
            using SemaphoreSlim stateLock = new(1, 1);

            async Task RunOneAsync((Scenario Scenario, int Sample, int Index) item, CancellationToken innerCt)
            {
                ScoreCard card = await ScoreOnceAsync(item.Scenario, spec.AgentName, agentDefinition, checkers, spec.Mode, item.Sample, innerCt, seedScenario, toolManager);
                results[item.Index] = card;

                await stateLock.WaitAsync(innerCt);
                try
                {
                    agentTokens += card.Efficiency is { } e ? e.InputTokens + e.OutputTokens : 0;
                    progress?.Report(new RefineryProgress($"[{++done}/{total}] {item.Scenario.Id} sample {item.Sample + 1}"));
                    campaign = campaign with { TokensSpent = agentTokens, MetaTokensSpent = _metaUsage.Spent };
                    await _store.SaveAsync(campaign, innerCt);
                }
                finally
                {
                    stateLock.Release();
                }
            }

            if (maxParallel <= 1)
            {
                foreach ((Scenario Scenario, int Sample, int Index) item in work)
                {
                    ct.ThrowIfCancellationRequested();
                    await RunOneAsync(item, ct);
                }
            }
            else
            {
                await Parallel.ForEachAsync(
                    work,
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
                    async (item, innerCt) => await RunOneAsync(item, innerCt));
            }

            cards.AddRange(results);

            await PersistScoreCardsAsync(id, cards, ct);
            SuiteAggregate aggregate = Aggregator.Aggregate(cards);
            campaign = campaign with
            {
                Status = CampaignStatus.Converged,
                Baseline = aggregate,
                Current = aggregate,
                TokensSpent = agentTokens,
                MetaTokensSpent = _metaUsage.Spent
            };
            await _store.SaveAsync(campaign, ct);
            progress?.Report(new RefineryProgress(
                $"baseline: invariant pass-rate {aggregate.InvariantPassRate:P0}, quality mean {aggregate.QualityMean:F1} (p10 {aggregate.QualityP10:F1}) over {aggregate.RunCount} runs" +
                (aggregate.QualityJudgeFailures > 0 ? $" — WARNING: {aggregate.QualityJudgeFailures} judge verdict(s) missing/unparsed" : ""),
                cards));
            return campaign;
        }
        catch (OperationCanceledException)
        {
            campaign = campaign with { Status = CampaignStatus.Stopped, TokensSpent = agentTokens, MetaTokensSpent = _metaUsage.Spent };
            await _store.SaveAsync(campaign, ct: default);
            throw;
        }
        catch (Exception ex)
        {
            campaign = campaign with { Status = CampaignStatus.Failed, Error = ex.Message, TokensSpent = agentTokens, MetaTokensSpent = _metaUsage.Spent };
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
    /// <b>Beam per round:</b> each round builds a small beam of candidates — the LLM proposal
    /// (<see cref="IProposalStrategy.ProposeAsync"/>, train-only) plus every <see cref="KnobMutators.All"/>
    /// typed mutator applied to the current definition — validates and scores each, runs the pairwise gate
    /// against the current baseline, and (among those that pass <see cref="GatePolicy"/>) accepts the single
    /// best by train <see cref="SuiteAggregate.QualityP10"/> (tie-break: higher pairwise net). Every candidate
    /// attempted is recorded as a <see cref="VariantRecord"/> + <see cref="LedgerEntry"/>.
    /// </para>
    /// <para>
    /// <b>Dual budgets:</b> agent-execution TOKENS (<see cref="AgentTrace.InputTokens"/> +
    /// <see cref="AgentTrace.OutputTokens"/> per run) are tracked by the agent governor, while the meta-LLM
    /// calls (judge / pairwise / proposer) are tracked separately via <see cref="MetaLlmUsageBroker"/> against
    /// <see cref="CampaignSpec.MetaTokenBudget"/>. Exhausting EITHER budget terminates the campaign with
    /// <see cref="CampaignStatus.BudgetExhausted"/>.
    /// </para>
    /// </summary>
    public async Task<Campaign> RunCampaignAsync(
        CampaignSpec spec,
        ScenarioSuite suite,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        IProgress<RefineryProgress>? progress = null,
        CancellationToken ct = default,
        string? campaignId = null,
        Func<Scenario, CancellationToken, Task>? seedScenario = null,
        IToolManager? toolManager = null)
    {
        string id = campaignId ?? Guid.NewGuid().ToString("n");

        // (a) Split scenarios. An empty held-out set means held-out metrics mirror train metrics.
        List<Scenario> train = suite.Scenarios.Where(s => !s.HeldOut).ToList();
        List<Scenario> heldOut = suite.Scenarios.Where(s => s.HeldOut).ToList();
        bool heldOutMirrorsTrain = heldOut.Count == 0;

        // A per-campaign label for candidate profiles. Candidates are now run DIRECTLY via the per-run profile
        // overload (no IAgentManager slot mutation), so this is a trace/validation label only — not a shared
        // registry key. Kept under the "__refinery/" prefix for diagnostic consistency.
        string tempName = "__refinery/" + id + "/candidate";

        Campaign campaign = new()
        {
            Id = id,
            Spec = spec,
            Status = CampaignStatus.Running
        };
        await _store.SaveAsync(campaign, ct);

        await PersistGroundTruthAsync(suite, ct);

        BudgetGovernor governor = new(spec.TokenBudget);
        BudgetGovernor metaGovernor = new(spec.MetaTokenBudget);
        int samples = Math.Max(1, spec.SamplesPerScenario);

        // Scenario-seeding plugins (IScenarioWorld) mutate a shared store per run, so seeded suites must
        // stay sequential; everything else parallelizes up to the spec's cap.
        int maxParallel = seedScenario is null ? Math.Max(1, spec.MaxParallelRuns) : 1;

        // (a) Open a meta-LLM usage scope for the whole campaign; the judge/pairwise/proposer accumulate into
        // it via MetaLlmUsageBroker. We sample the scope's running total per round and charge the DELTA to
        // metaGovernor so the dedicated meta budget is enforced alongside the agent-token governor.
        using MetaLlmUsageBroker.Scope metaScope = _metaUsage.BeginScope();
        long metaSpentLastSnapshot = 0;

        // Persist the live meters after every scored run so `refine status` answers "how much have I
        // spent?" and "is the judge working?" WHILE the campaign runs, not only at round boundaries.
        async Task PersistSpendAsync()
        {
            campaign = campaign with { TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent };
            await _store.SaveAsync(campaign, ct);
        }

        try
        {
            // (b) Baseline: score train + held-out once each, keeping per-scenario train OUTPUT text for pairwise.
            string currentDefinition = agentDefinition;

            (List<ScoreCard> baselineTrainCards, Dictionary<string, string> baselineTrainOutputs) =
                await ScoreSetAsync(train, spec.AgentName, currentDefinition, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager, PersistSpendAsync, maxParallel);

            List<ScoreCard> baselineHeldOutCards = heldOutMirrorsTrain
                ? baselineTrainCards
                : (await ScoreSetAsync(heldOut, spec.AgentName, currentDefinition, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager, PersistSpendAsync, maxParallel)).Cards;

            await PersistScoreCardsAsync(id, baselineTrainCards, ct);
            if (!heldOutMirrorsTrain)
                await PersistScoreCardsAsync(id, baselineHeldOutCards, ct);

            SuiteAggregate baselineTrain = Aggregator.Aggregate(baselineTrainCards);
            SuiteAggregate baselineHeldOut = Aggregator.Aggregate(baselineHeldOutCards);

            // Roll any meta tokens spent measuring the baseline into the meta governor.
            ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot);

            campaign = campaign with { Baseline = baselineTrain, Current = baselineTrain, TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent };
            await _store.SaveAsync(campaign, ct);
            progress?.Report(new RefineryProgress(
                $"baseline: invariant pass-rate {baselineTrain.InvariantPassRate:P0}, quality p10 {baselineTrain.QualityP10:F1} over {baselineTrain.RunCount} train runs" +
                (baselineTrain.QualityJudgeFailures > 0 ? $" — WARNING: {baselineTrain.QualityJudgeFailures} judge verdict(s) missing/unparsed" : ""),
                baselineTrainCards));

            List<ScoreCard> currentTrainCards = baselineTrainCards;
            int noImprove = 0;

            // (c) Improvement rounds.
            for (int round = 1; round <= spec.MaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                // Budget gate at the top of the round — either budget exhausting stops the campaign.
                if (BudgetsExhausted(governor, metaGovernor, out string topReason))
                {
                    campaign = campaign with { Status = CampaignStatus.BudgetExhausted, TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent };
                    await _store.SaveAsync(campaign, ct);
                    progress?.Report(new RefineryProgress($"round {round}: {topReason}"));
                    return campaign;
                }

                progress?.Report(new RefineryProgress($"round {round}: building candidate beam…"));

                // (b·1) BEAM: the LLM proposal (train-only) PLUS every typed mutator on the current definition.
                List<Proposal> candidates = await BuildBeamAsync(spec.AgentName, currentDefinition, baselineTrain, currentTrainCards, ct);
                ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot); // proposer meta cost

                if (candidates.Count == 0)
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
                        TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent
                    };
                    await _store.SaveAsync(campaign, ct);
                    progress?.Report(new RefineryProgress($"round {round}: no candidates (no-improvement {noImprove}/{spec.StopAfterNoImprovementRounds})"));
                    if (noImprove >= spec.StopAfterNoImprovementRounds) break;
                    continue;
                }

                // (b·2) Evaluate each candidate: validate → register → score → pairwise → gate. Record a
                // VariantRecord + LedgerEntry for EVERY candidate attempted; track the best passing one.
                List<VariantRecord> roundVariants = [];
                BeamWinner? best = null;
                bool budgetStop = false;

                foreach (Proposal proposal in candidates)
                {
                    ct.ThrowIfCancellationRequested();

                    // Enforce budgets BEFORE spending any run/meta budget on this candidate, so a round cannot
                    // blow the budget mid-beam.
                    if (BudgetsExhausted(governor, metaGovernor, out string midReason))
                    {
                        budgetStop = true;
                        progress?.Report(new RefineryProgress($"round {round}: {midReason}"));
                        break;
                    }

                    // One id per candidate: it tags the VariantRecord AND is stamped as the AgentVersion on this
                    // candidate's score cards, so per-variant calibration can join them.
                    string variantId = Guid.NewGuid().ToString("n");

                    ValidationResult validation = _validator.Validate(proposal.RevisedContent, tempName);
                    if (!validation.Ok)
                    {
                        string errors = validation.Errors.Count > 0 ? string.Join("; ", validation.Errors) : "candidate failed validation";
                        roundVariants.Add(await RecordCandidateAsync(id, variantId, round, spec.AgentName, proposal,
                            candTrain: null, candHeldOut: null, accepted: false, reason: errors, governor.Spent, ct));
                        progress?.Report(new RefineryProgress($"round {round}: candidate ({proposal.KnobType}) rejected by validator — {errors}"));
                        continue;
                    }

                    // Per-run isolation: parse the candidate source to a profile and run it DIRECTLY (no shared
                    // IAgentManager slot mutation). The same per-run toolManager is used so candidate runs see
                    // exactly the baseline's isolated tool set.
                    AgentProfile candidateProfile = ParseCandidateProfile(proposal.RevisedContent, tempName);

                    // (b·2a) SCREEN — cheap 1-sample train pre-pass. Clear losers are rejected here for ~1/5
                    // of a full evaluation's cost; anything not clearly worse proceeds. Screen cards are NOT
                    // persisted (single-sample noise would pollute per-variant calibration).
                    if (spec.ScreenCandidates && samples > 1)
                    {
                        (List<ScoreCard> screenCards, _) =
                            await ScoreSetAsync(train, candidateProfile, spec.AgentName, variantId, proposal.RevisedContent, checkers, spec.Mode, 1, governor, ct, seedScenario, toolManager, PersistSpendAsync, maxParallel);
                        SuiteAggregate screenAgg = Aggregator.Aggregate(screenCards);
                        ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot); // screen judge cost

                        GateDecision screenDecision = GatePolicy.DecideScreen(baselineTrain, screenAgg);
                        if (!screenDecision.Accept)
                        {
                            roundVariants.Add(await RecordCandidateAsync(id, variantId, round, spec.AgentName, proposal,
                                screenAgg, candHeldOut: null, accepted: false, reason: screenDecision.Reason, governor.Spent, ct));
                            progress?.Report(new RefineryProgress($"round {round}: candidate ({proposal.KnobType}) rejected at screen — {screenDecision.Reason}", screenCards));
                            continue;
                        }
                    }

                    // (b·2b) Full train scoring. Cards are filed under the REAL agent + this variant id (not
                    // the transient candidate profile name), so calibration can slice by variant.
                    (List<ScoreCard> candTrainCards, Dictionary<string, string> candTrainOutputs) =
                        await ScoreSetAsync(train, candidateProfile, spec.AgentName, variantId, proposal.RevisedContent, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager, PersistSpendAsync, maxParallel);
                    await PersistScoreCardsAsync(id, candTrainCards, ct);
                    SuiteAggregate candTrain = Aggregator.Aggregate(candTrainCards);
                    ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot); // train judge cost

                    // (b·2c) FAIL FAST, cheapest evidence first: train-side gate needs nothing further.
                    GateDecision trainDecision = GatePolicy.DecideTrain(baselineTrain, candTrain);
                    if (!trainDecision.Accept)
                    {
                        roundVariants.Add(await RecordCandidateAsync(id, variantId, round, spec.AgentName, proposal,
                            candTrain, candHeldOut: null, accepted: false, reason: trainDecision.Reason, governor.Spent, ct));
                        progress?.Report(new RefineryProgress($"round {round}: candidate ({proposal.KnobType}) rejected — {trainDecision.Reason}", candTrainCards));
                        continue;
                    }

                    // (b·2d) Pairwise gate next (≤8 LLM calls) — still cheaper than scoring the held-out set.
                    int pairwiseNet = await PairwiseNetAsync(train, baselineTrainOutputs, candTrainOutputs, ct);
                    ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot); // pairwise meta cost

                    GateDecision pairwiseDecision = GatePolicy.DecidePairwise(pairwiseNet);
                    if (!pairwiseDecision.Accept)
                    {
                        roundVariants.Add(await RecordCandidateAsync(id, variantId, round, spec.AgentName, proposal,
                            candTrain, candHeldOut: null, accepted: false, reason: pairwiseDecision.Reason, governor.Spent, ct));
                        progress?.Report(new RefineryProgress($"round {round}: candidate ({proposal.KnobType}) rejected — {pairwiseDecision.Reason}", candTrainCards));
                        continue;
                    }

                    // (b·2e) Held-out — the most expensive evidence, gathered only for survivors.
                    List<ScoreCard> candHeldOutCards = heldOutMirrorsTrain
                        ? candTrainCards
                        : (await ScoreSetAsync(heldOut, candidateProfile, spec.AgentName, variantId, proposal.RevisedContent, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager, PersistSpendAsync, maxParallel)).Cards;
                    if (!heldOutMirrorsTrain)
                        await PersistScoreCardsAsync(id, candHeldOutCards, ct);
                    SuiteAggregate candHeldOut = Aggregator.Aggregate(candHeldOutCards);
                    ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot); // held-out judge cost

                    GateDecision heldOutDecision = GatePolicy.DecideHeldOut(baselineHeldOut, candHeldOut);
                    string decisionReason = heldOutDecision.Accept
                        ? $"Accepted: train p10 {candTrain.QualityP10:F4} > {baselineTrain.QualityP10:F4}, " +
                          $"held-out p10 {candHeldOut.QualityP10:F4} >= {baselineHeldOut.QualityP10:F4}, " +
                          $"invariants non-regressed, pairwise net {pairwiseNet} > 0."
                        : heldOutDecision.Reason;

                    VariantRecord record = await RecordCandidateAsync(id, variantId, round, spec.AgentName, proposal,
                        candTrain, candHeldOut, heldOutDecision.Accept, decisionReason, governor.Spent, ct);
                    roundVariants.Add(record);

                    if (heldOutDecision.Accept)
                    {
                        BeamWinner contender = new(record.Id, proposal, candTrain, candHeldOut, candTrainCards, candTrainOutputs, pairwiseNet);
                        if (best is null || IsBetter(contender, best))
                            best = contender;
                    }
                    else
                    {
                        progress?.Report(new RefineryProgress($"round {round}: candidate ({proposal.KnobType}) rejected — {heldOutDecision.Reason}", candTrainCards));
                    }
                }

                // (b·3) Persist the round's iteration with ALL candidate variants and the accepted-best (if any).
                string? acceptedId = best?.VariantId;
                campaign = campaign with
                {
                    Iterations =
                    [
                        .. campaign.Iterations,
                        new CampaignIteration
                        {
                            Round = round,
                            Baseline = baselineTrain,
                            Variants = roundVariants,
                            AcceptedVariantId = acceptedId
                        }
                    ],
                    TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent
                };

                // (b·4) Adopt the best passing candidate, or count a no-improvement round.
                if (best is { } winner)
                {
                    currentDefinition = winner.Proposal.RevisedContent;
                    baselineTrain = winner.Train;
                    baselineHeldOut = winner.HeldOut;
                    currentTrainCards = winner.TrainCards;
                    baselineTrainOutputs = winner.TrainOutputs;
                    campaign = campaign with { Current = winner.Train };
                    noImprove = 0;
                    progress?.Report(new RefineryProgress($"round {round}: ACCEPTED ({winner.Proposal.KnobType}) — best of {roundVariants.Count} candidate(s)", winner.TrainCards));
                }
                else
                {
                    noImprove++;
                    progress?.Report(new RefineryProgress($"round {round}: no candidate passed the gate ({roundVariants.Count} tried) (no-improvement {noImprove}/{spec.StopAfterNoImprovementRounds})"));
                }

                await _store.SaveAsync(campaign, ct);

                // Budget gate after the round (or if a mid-beam break tripped it).
                string endReason = "";
                if (budgetStop || BudgetsExhausted(governor, metaGovernor, out endReason))
                {
                    string reason = budgetStop ? "token budget exhausted" : endReason;
                    campaign = campaign with { Status = CampaignStatus.BudgetExhausted, TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent };
                    await _store.SaveAsync(campaign, ct);
                    progress?.Report(new RefineryProgress($"round {round}: {reason}"));
                    return campaign;
                }

                if (noImprove >= spec.StopAfterNoImprovementRounds)
                    break;
            }

            // (h) Terminal status — Converged unless an earlier branch already set a terminal state.
            campaign = campaign with { Status = CampaignStatus.Converged, TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent };
            await _store.SaveAsync(campaign, ct);
            progress?.Report(new RefineryProgress(
                $"campaign converged: current quality p10 {baselineTrain.QualityP10:F1}, {governor.Spent} agent tokens / {metaGovernor.Spent} meta tokens spent"));
            return campaign;
        }
        catch (OperationCanceledException)
        {
            campaign = campaign with { Status = CampaignStatus.Stopped, TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent };
            await _store.SaveAsync(campaign, ct: default);
            throw;
        }
        catch (Exception ex)
        {
            campaign = campaign with { Status = CampaignStatus.Failed, Error = ex.Message, TokensSpent = governor.Spent, MetaTokensSpent = _metaUsage.Spent };
            await _store.SaveAsync(campaign, ct: default);
            throw;
        }
    }

    /// <summary>
    /// Build the per-round candidate beam: the LLM proposal (train-only, may be null) plus every typed
    /// <see cref="KnobMutators.All"/> mutator applied to the current definition. Null candidates and any over
    /// the <see cref="MaxCandidatesPerRound"/> ceiling are dropped.
    /// </summary>
    private async Task<List<Proposal>> BuildBeamAsync(
        string agentName,
        string currentDefinition,
        SuiteAggregate baselineTrain,
        IReadOnlyList<ScoreCard> currentTrainCards,
        CancellationToken ct)
    {
        List<Proposal> candidates = [];

        Proposal? llm = await _proposer.ProposeAsync(agentName, currentDefinition, baselineTrain, currentTrainCards, ct);
        if (llm is not null)
            candidates.Add(llm);

        foreach (ICandidateMutator mutator in KnobMutators.All())
        {
            if (candidates.Count >= MaxCandidatesPerRound) break;
            Proposal? mutated = mutator.Mutate(agentName, currentDefinition, baselineTrain, currentTrainCards);
            if (mutated is not null)
                candidates.Add(mutated);
        }

        return candidates;
    }

    /// <summary>The best passing candidate found within a round (for the beam tie-break).</summary>
    private sealed record BeamWinner(
        string VariantId,
        Proposal Proposal,
        SuiteAggregate Train,
        SuiteAggregate HeldOut,
        List<ScoreCard> TrainCards,
        Dictionary<string, string> TrainOutputs,
        int PairwiseNet);

    /// <summary>
    /// Ranks beam candidates: higher train <see cref="SuiteAggregate.QualityP10"/> wins; ties broken by higher
    /// pairwise net advantage.
    /// </summary>
    private static bool IsBetter(BeamWinner a, BeamWinner b) =>
        a.Train.QualityP10 > b.Train.QualityP10 ||
        (a.Train.QualityP10 == b.Train.QualityP10 && a.PairwiseNet > b.PairwiseNet);

    /// <summary>True when the agent OR meta token budget is exhausted; <paramref name="reason"/> names which.</summary>
    private static bool BudgetsExhausted(BudgetGovernor governor, BudgetGovernor metaGovernor, out string reason)
    {
        if (governor.Exhausted)
        {
            reason = $"agent token budget exhausted ({governor.Spent} tokens)";
            return true;
        }
        if (metaGovernor.Exhausted)
        {
            reason = $"meta token budget exhausted ({metaGovernor.Spent} tokens)";
            return true;
        }
        reason = "";
        return false;
    }

    /// <summary>
    /// Capture score cards for calibration / knob-effectiveness analysis when the store supports it
    /// (<see cref="IScoreCardSource"/>). A no-op for stores that do not implement the capability, so the
    /// campaign + ledger persistence on <see cref="ICampaignStore"/> is unaffected. Called with baseline cards
    /// (filed under the real agent, no version) and candidate cards (filed under the real agent + variant id),
    /// so calibration can query the agent overall or slice by a specific variant.
    /// </summary>
    private Task PersistScoreCardsAsync(string campaignId, IReadOnlyList<ScoreCard> cards, CancellationToken ct) =>
        cards.Count > 0 && _store is IScoreCardSource sink
            ? sink.SaveScoreCardsAsync(campaignId, cards, ct)
            : Task.CompletedTask;

    /// <summary>
    /// Capture the suite's scenario ground truth (the objective answer for scenarios that carry one) so
    /// calibration can join each fact-checker run to its known-correct winner. No-op when the store does not
    /// implement <see cref="IScoreCardSource"/> or no scenario carries ground truth.
    /// </summary>
    private Task PersistGroundTruthAsync(ScenarioSuite suite, CancellationToken ct)
    {
        if (_store is not IScoreCardSource sink)
            return Task.CompletedTask;

        Dictionary<string, string> truth = [];
        foreach (Scenario s in suite.Scenarios)
            if (!string.IsNullOrEmpty(s.GroundTruth))
                truth[s.Id] = s.GroundTruth;

        return truth.Count > 0 ? sink.SaveGroundTruthAsync(truth, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Charge the meta tokens spent since the last snapshot (the broker scope's running total minus the prior
    /// snapshot) to <paramref name="metaGovernor"/>, then advance the snapshot.
    /// </summary>
    private void ChargeMetaDelta(BudgetGovernor metaGovernor, ref long lastSnapshot)
    {
        long now = _metaUsage.Spent;
        long delta = now - lastSnapshot;
        if (delta > 0)
            metaGovernor.Record(delta);
        lastSnapshot = now;
    }

    /// <summary>
    /// Run one scenario sample and produce its full <see cref="ScoreCard"/>. <paramref name="agentName"/> is
    /// the campaign-level agent under test (<c>spec.AgentName</c>) — the SAME source of truth
    /// <see cref="RunCampaignAsync"/> uses — so a scenario whose own <see cref="Scenario.AgentName"/> drifts
    /// from the suite can never silently measure a different agent.
    /// </summary>
    public async Task<ScoreCard> ScoreOnceAsync(
        Scenario scenario,
        string agentName,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int sampleIndex,
        CancellationToken ct = default,
        Func<Scenario, CancellationToken, Task>? seedScenario = null,
        IToolManager? toolManager = null)
    {
        if (seedScenario != null) await seedScenario(scenario, ct);
        AgentRun run = await RunScenarioAsync(scenario, agentName, agentProfile: null, toolManager, mode, ct);
        IReadOnlyList<InvariantResult> invariants = StructuralScorer.Score(run.Trace, scenario, checkers);
        QualityScore? quality = scenario.Rubric.Count > 0
            ? await _judge.JudgeAsync(run.Trace, agentDefinition, scenario, invariants, ct)
            : null;
        EfficiencyMetrics efficiency = EfficiencyExtractor.Extract(run.Trace, run.LatencyMs);
        return ScoreCardBuilder.Build(scenario, run.Trace, invariants, quality, efficiency, sampleIndex, mode);
    }

    /// <summary>
    /// Like <see cref="ScoreOnceAsync"/> but ALSO returns the run's final output text (needed for the pairwise
    /// gate, which <see cref="ScoreCard"/> does not carry). When <paramref name="agentProfile"/> is non-null
    /// the run goes through the per-run profile overload (no shared registry slot) — used for CANDIDATE runs;
    /// otherwise it is a name-based run of <paramref name="agentName"/>. The optional
    /// <paramref name="toolManager"/> isolates the run's tool registry. The given
    /// <paramref name="agentDefinition"/> is the text shown to the LLM judge.
    /// </summary>
    private async Task<(ScoreCard Card, string Output)> RunAndScoreAsync(
        Scenario scenario,
        string agentName,
        AgentProfile? agentProfile,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int sampleIndex,
        CancellationToken ct,
        Func<Scenario, CancellationToken, Task>? seedScenario,
        IToolManager? toolManager,
        string? cardAgentName = null,
        string? cardAgentVersion = null)
    {
        if (seedScenario != null) await seedScenario(scenario, ct);
        AgentRun run = await RunScenarioAsync(scenario, agentName, agentProfile, toolManager, mode, ct);
        IReadOnlyList<InvariantResult> invariants = StructuralScorer.Score(run.Trace, scenario, checkers);
        QualityScore? quality = scenario.Rubric.Count > 0
            ? await _judge.JudgeAsync(run.Trace, agentDefinition, scenario, invariants, ct)
            : null;
        EfficiencyMetrics efficiency = EfficiencyExtractor.Extract(run.Trace, run.LatencyMs);
        ScoreCard card = ScoreCardBuilder.Build(
            scenario, run.Trace, invariants, quality, efficiency, sampleIndex, mode, cardAgentVersion, cardAgentName);
        return (card, run.Trace.FinalOutput ?? "");
    }

    /// <summary>
    /// Dispatch a single scenario run with per-run isolation. CANDIDATE runs (<paramref name="agentProfile"/>
    /// non-null) ALWAYS go through the profile overload so no shared <see cref="IAgentManager"/> slot is
    /// mutated, carrying the per-run <paramref name="toolManager"/> as the tool override. Baseline /
    /// non-candidate runs are name-based; when a <paramref name="toolManager"/> is supplied they are routed
    /// through the same profile overload using the agent's registered profile (resolved from
    /// <see cref="IAgentManager"/>) so they too see the isolated tool set — falling back to the pure
    /// name-based path when neither an override tool set nor a resolvable profile is available (keeping the
    /// unmodified name-based behaviour for callers that pass no toolManager, e.g. unit tests).
    /// <para>
    /// <b>Replay (Wave-3b):</b> when <paramref name="mode"/> is <c>"replay"</c> AND the scenario carries a
    /// non-empty <see cref="Scenario.ReplayScript"/>, the run is served entirely from the script with NO live
    /// inference. A scripted <see cref="ModelProfile"/> is built via
    /// <see cref="ReplayInference.BuildModel"/> and threaded through the
    /// Wave-3a per-run <c>modelOverride</c> seam on the profile overload: the agent-under-test profile is the
    /// candidate profile when one is supplied, otherwise the agent's registered profile. Live mode (the
    /// default) — or replay mode with no script — is UNCHANGED.
    /// </para>
    /// </summary>
    private async Task<AgentRun> RunScenarioAsync(
        Scenario scenario,
        string agentName,
        AgentProfile? agentProfile,
        IToolManager? toolManager,
        string mode,
        CancellationToken ct)
    {
        // Replay: serve the run from the scenario's scripted turns (no live provider). Uses the Wave-3a
        // per-run modelOverride seam — the profile-under-test (candidate, or the registered baseline profile)
        // is run with a scripted ModelProfile so every LLM call resolves to the next scripted assistant turn.
        if (string.Equals(mode, "replay", StringComparison.Ordinal) && scenario.ReplayScript is { Count: > 0 } script)
        {
            AgentProfile underTest = agentProfile ?? _agents.Get(agentName)
                ?? throw new InvalidOperationException(
                    $"Replay run for scenario '{scenario.Id}' could not resolve an AgentProfile for agent '{agentName}'.");

            // Map each SDK turn (Revi.Refinery.ReplayTurn) onto the Core replay turn (global::Revi.ReplayTurn)
            // the inference seam consumes. The two records are intentionally separate so the Core layer never
            // references the SDK; this is the single boundary that bridges them.
            List<global::Revi.ReplayTurn> coreScript = script
                .Select(t => new global::Revi.ReplayTurn
                {
                    Signal = t.Signal,
                    Content = t.Content,
                    ToolCalls = t.ToolCalls,
                    PromptTokens = t.PromptTokens,
                    CompletionTokens = t.CompletionTokens,
                })
                .ToList();

            ModelProfile replayModel = ReplayInference.BuildModel("__replay/" + scenario.Id, coreScript);
            return await _runner.RunOnceAsync(underTest, scenario.Inputs, toolManager, replayModel, ct);
        }

        // Candidate: run the parsed profile directly (no registry mutation).
        if (agentProfile is not null)
            return await _runner.RunOnceAsync(agentProfile, scenario.Inputs, toolManager, model: null, ct);

        // Baseline / non-candidate with tool isolation: resolve the registered profile and run it directly so
        // the per-run toolManager applies. The profile is used READ-ONLY (not mutated) so the shared registry
        // entry is untouched; its own Name (which equals the lookup key) carries through to the trace.
        if (toolManager is not null && _agents.Get(agentName) is { } registered)
            return await _runner.RunOnceAsync(registered, scenario.Inputs, toolManager, model: null, ct);

        // No isolation requested (or no resolvable profile): the original, unchanged name-based path.
        return await _runner.RunOnceAsync(agentName, scenario.Inputs, ct);
    }

    /// <summary>
    /// Score a whole scenario set (every scenario × <paramref name="samples"/>), charging
    /// agent-execution tokens to <paramref name="governor"/>. Returns the score cards plus the LAST sample's
    /// output text per scenario id (a stable per-scenario representative for pairwise comparison).
    /// </summary>
    private Task<(List<ScoreCard> Cards, Dictionary<string, string> Outputs)> ScoreSetAsync(
        IReadOnlyList<Scenario> scenarios,
        string agentName,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int samples,
        BudgetGovernor governor,
        CancellationToken ct,
        Func<Scenario, CancellationToken, Task>? seedScenario,
        IToolManager? toolManager = null,
        Func<Task>? persistSpend = null,
        int maxParallel = 1)
        => ScoreSetCoreAsync(scenarios, agentName, agentProfile: null, agentDefinition, checkers, mode, samples, governor, ct, seedScenario, toolManager, persistSpend: persistSpend, maxParallel: maxParallel);

    /// <summary>
    /// Candidate overload of <see cref="ScoreSetAsync(IReadOnlyList{Scenario}, string, string, IReadOnlyList{IInvariantChecker}, string, int, BudgetGovernor, CancellationToken, Func{Scenario, CancellationToken, Task}?, IToolManager?)"/>:
    /// scores the whole set by running the parsed candidate <paramref name="agentProfile"/> DIRECTLY (via the
    /// per-run profile overload, no shared registry slot), carrying the per-run <paramref name="toolManager"/>.
    /// The produced cards are filed under <paramref name="realAgentName"/> + <paramref name="variantId"/> (the
    /// VariantRecord id) — NOT the transient candidate profile name — so per-variant calibration/analysis works.
    /// </summary>
    private Task<(List<ScoreCard> Cards, Dictionary<string, string> Outputs)> ScoreSetAsync(
        IReadOnlyList<Scenario> scenarios,
        AgentProfile agentProfile,
        string realAgentName,
        string variantId,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int samples,
        BudgetGovernor governor,
        CancellationToken ct,
        Func<Scenario, CancellationToken, Task>? seedScenario,
        IToolManager? toolManager = null,
        Func<Task>? persistSpend = null,
        int maxParallel = 1)
        => ScoreSetCoreAsync(scenarios, agentProfile.Name ?? "", agentProfile, agentDefinition, checkers, mode, samples, governor, ct, seedScenario, toolManager, realAgentName, variantId, persistSpend, maxParallel);

    /// <summary>
    /// Shared body for both <c>ScoreSetAsync</c> overloads (name-based and candidate-profile). When
    /// <paramref name="cardAgentName"/> / <paramref name="cardAgentVersion"/> are supplied (candidate runs),
    /// every produced card is filed under that real agent + variant id rather than the transient run profile.
    /// </summary>
    private async Task<(List<ScoreCard> Cards, Dictionary<string, string> Outputs)> ScoreSetCoreAsync(
        IReadOnlyList<Scenario> scenarios,
        string agentName,
        AgentProfile? agentProfile,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int samples,
        BudgetGovernor governor,
        CancellationToken ct,
        Func<Scenario, CancellationToken, Task>? seedScenario,
        IToolManager? toolManager,
        string? cardAgentName = null,
        string? cardAgentVersion = null,
        Func<Task>? persistSpend = null,
        int maxParallel = 1)
    {
        // Flatten the (scenario × sample) grid up front so results reassemble in a DETERMINISTIC order
        // regardless of completion order — cards and the per-scenario representative output are identical
        // whether the set ran sequentially or in parallel.
        List<(Scenario Scenario, int Sample, int Index)> work = [];
        int nextIndex = 0;
        foreach (Scenario scenario in scenarios)
            for (int sample = 0; sample < samples; sample++)
                work.Add((scenario, sample, nextIndex++));

        var results = new (ScoreCard Card, string Output)[work.Count];

        // persistSpend mutates and saves the campaign record — serialize those calls across runners.
        using SemaphoreSlim persistLock = new(1, 1);

        async Task RunOneAsync((Scenario Scenario, int Sample, int Index) item, CancellationToken innerCt)
        {
            (ScoreCard card, string output) = await RunAndScoreAsync(
                item.Scenario, agentName, agentProfile, agentDefinition, checkers, mode, item.Sample,
                innerCt, seedScenario, toolManager, cardAgentName, cardAgentVersion);
            results[item.Index] = (card, output);
            governor.Record(card.Efficiency is { } e ? e.InputTokens + e.OutputTokens : 0);

            // Publish the live spend meters after every run (see PersistSpendAsync in RunCampaignAsync).
            if (persistSpend is not null)
            {
                await persistLock.WaitAsync(innerCt);
                try { await persistSpend(); }
                finally { persistLock.Release(); }
            }
        }

        if (maxParallel <= 1)
        {
            foreach ((Scenario Scenario, int Sample, int Index) item in work)
            {
                ct.ThrowIfCancellationRequested();
                await RunOneAsync(item, ct);
            }
        }
        else
        {
            // Each parallel body runs in its own async flow, so the per-run trace capture
            // (RefineryCaptureBroker, AsyncLocal) and the campaign meta-usage scope both isolate/aggregate
            // correctly; BudgetGovernor.Record is Interlocked.
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
                async (item, innerCt) => await RunOneAsync(item, innerCt));
        }

        List<ScoreCard> cards = new(work.Count);
        Dictionary<string, string> outputs = [];
        foreach ((Scenario Scenario, int Sample, int Index) item in work)
        {
            (ScoreCard card, string output) = results[item.Index];
            cards.Add(card);
            outputs[item.Scenario.Id] = output; // last sample wins — a representative output for pairwise
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

    /// <summary>
    /// Parse a proposed revised .agent SOURCE into an <see cref="AgentProfile"/> the candidate is run from.
    /// <para>
    /// Replaces the former shared-slot registration (<see cref="IAgentManager.AddOrReplace"/>): the profile is
    /// run DIRECTLY via the per-run profile overload, so concurrent candidates never collide on a single
    /// registry slot and no shared agent-registry state is mutated. The temp name is a label only — the
    /// internal graph does not reference it.
    /// </para>
    /// </summary>
    private static AgentProfile ParseCandidateProfile(string revisedSource, string tempName)
    {
        Dictionary<string, string> data = RConfigParser.ReadEmbedded(revisedSource);
        AgentProfile candidate = AgentProfile.ToObject(data, namePrefix: "");
        candidate.Name = tempName;          // label for traces/diagnostics only; not a shared registration key
        return candidate;
    }

    /// <summary>
    /// Append one candidate's ledger entry and return its <see cref="VariantRecord"/> (the caller batches the
    /// round's variants into a single <see cref="CampaignIteration"/>). Covers both validation-failure
    /// rejections (null scores) and gated candidates (scored, accepted or rejected).
    /// </summary>
    private async Task<VariantRecord> RecordCandidateAsync(
        string campaignId,
        string variantId,
        int round,
        string agentName,
        Proposal proposal,
        SuiteAggregate? candTrain,
        SuiteAggregate? candHeldOut,
        bool accepted,
        string reason,
        long tokensSpent,
        CancellationToken ct)
    {
        VariantRecord variant = new()
        {
            // Caller-supplied so the variant id matches the AgentVersion stamped on this candidate's score
            // cards (per-variant calibration joins the two).
            Id = variantId,
            AgentName = agentName,
            Round = round,
            KnobType = proposal.KnobType,
            Diff = proposal.Diff,
            RevisedContent = proposal.RevisedContent,
            TrainScores = candTrain,
            HeldOutScores = candHeldOut,
            Accepted = accepted,
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
            Accepted = accepted,
            RejectReason = accepted ? null : reason,
            TokensSpent = tokensSpent
        }, ct);

        return variant;
    }

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
