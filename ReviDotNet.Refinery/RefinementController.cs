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

                    ScoreCard card = await ScoreOnceAsync(scenario, agentDefinition, checkers, spec.Mode, sample, ct, seedScenario, toolManager);
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

        BudgetGovernor governor = new(spec.TokenBudget);
        BudgetGovernor metaGovernor = new(spec.MetaTokenBudget);
        int samples = Math.Max(1, spec.SamplesPerScenario);

        // (a) Open a meta-LLM usage scope for the whole campaign; the judge/pairwise/proposer accumulate into
        // it via MetaLlmUsageBroker. We sample the scope's running total per round and charge the DELTA to
        // metaGovernor so the dedicated meta budget is enforced alongside the agent-token governor.
        using MetaLlmUsageBroker.Scope metaScope = _metaUsage.BeginScope();
        long metaSpentLastSnapshot = 0;

        try
        {
            // (b) Baseline: score train + held-out once each, keeping per-scenario train OUTPUT text for pairwise.
            string currentDefinition = agentDefinition;

            (List<ScoreCard> baselineTrainCards, Dictionary<string, string> baselineTrainOutputs) =
                await ScoreSetAsync(train, spec.AgentName, currentDefinition, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager);

            List<ScoreCard> baselineHeldOutCards = heldOutMirrorsTrain
                ? baselineTrainCards
                : (await ScoreSetAsync(heldOut, spec.AgentName, currentDefinition, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager)).Cards;

            SuiteAggregate baselineTrain = Aggregator.Aggregate(baselineTrainCards);
            SuiteAggregate baselineHeldOut = Aggregator.Aggregate(baselineHeldOutCards);

            // Roll any meta tokens spent measuring the baseline into the meta governor.
            ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot);

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

                // Budget gate at the top of the round — either budget exhausting stops the campaign.
                if (BudgetsExhausted(governor, metaGovernor, out string topReason))
                {
                    campaign = campaign with { Status = CampaignStatus.BudgetExhausted, TokensSpent = governor.Spent };
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
                        TokensSpent = governor.Spent
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

                    ValidationResult validation = _validator.Validate(proposal.RevisedContent, tempName);
                    if (!validation.Ok)
                    {
                        string errors = validation.Errors.Count > 0 ? string.Join("; ", validation.Errors) : "candidate failed validation";
                        roundVariants.Add(await RecordCandidateAsync(id, round, spec.AgentName, proposal,
                            candTrain: null, candHeldOut: null, accepted: false, reason: errors, governor.Spent, ct));
                        progress?.Report(new RefineryProgress($"round {round}: candidate ({proposal.KnobType}) rejected by validator — {errors}"));
                        continue;
                    }

                    // Per-run isolation: parse the candidate source to a profile and run it DIRECTLY (no shared
                    // IAgentManager slot mutation). The same per-run toolManager is used so candidate runs see
                    // exactly the baseline's isolated tool set.
                    AgentProfile candidateProfile = ParseCandidateProfile(proposal.RevisedContent, tempName);

                    (List<ScoreCard> candTrainCards, Dictionary<string, string> candTrainOutputs) =
                        await ScoreSetAsync(train, candidateProfile, proposal.RevisedContent, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager);

                    List<ScoreCard> candHeldOutCards = heldOutMirrorsTrain
                        ? candTrainCards
                        : (await ScoreSetAsync(heldOut, candidateProfile, proposal.RevisedContent, checkers, spec.Mode, samples, governor, ct, seedScenario, toolManager)).Cards;

                    SuiteAggregate candTrain = Aggregator.Aggregate(candTrainCards);
                    SuiteAggregate candHeldOut = Aggregator.Aggregate(candHeldOutCards);

                    int pairwiseNet = await PairwiseNetAsync(train, baselineTrainOutputs, candTrainOutputs, ct);
                    ChargeMetaDelta(metaGovernor, ref metaSpentLastSnapshot); // judge + pairwise meta cost

                    GateDecision decision = GatePolicy.Decide(baselineTrain, candTrain, baselineHeldOut, candHeldOut, pairwiseNet);

                    VariantRecord record = await RecordCandidateAsync(id, round, spec.AgentName, proposal,
                        candTrain, candHeldOut, decision.Accept, decision.Reason, governor.Spent, ct);
                    roundVariants.Add(record);

                    if (decision.Accept)
                    {
                        BeamWinner contender = new(record.Id, proposal, candTrain, candHeldOut, candTrainCards, candTrainOutputs, pairwiseNet);
                        if (best is null || IsBetter(contender, best))
                            best = contender;
                    }
                    else
                    {
                        progress?.Report(new RefineryProgress($"round {round}: candidate ({proposal.KnobType}) rejected — {decision.Reason}", candTrainCards));
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
                    TokensSpent = governor.Spent
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
                    campaign = campaign with { Status = CampaignStatus.BudgetExhausted, TokensSpent = governor.Spent };
                    await _store.SaveAsync(campaign, ct);
                    progress?.Report(new RefineryProgress($"round {round}: {reason}"));
                    return campaign;
                }

                if (noImprove >= spec.StopAfterNoImprovementRounds)
                    break;
            }

            // (h) Terminal status — Converged unless an earlier branch already set a terminal state.
            campaign = campaign with { Status = CampaignStatus.Converged, TokensSpent = governor.Spent };
            await _store.SaveAsync(campaign, ct);
            progress?.Report(new RefineryProgress(
                $"campaign converged: current quality p10 {baselineTrain.QualityP10:F1}, {governor.Spent} agent tokens / {metaGovernor.Spent} meta tokens spent"));
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

    /// <summary>Run one scenario sample and produce its full <see cref="ScoreCard"/>.</summary>
    public async Task<ScoreCard> ScoreOnceAsync(
        Scenario scenario,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int sampleIndex,
        CancellationToken ct = default,
        Func<Scenario, CancellationToken, Task>? seedScenario = null,
        IToolManager? toolManager = null)
    {
        if (seedScenario != null) await seedScenario(scenario, ct);
        AgentRun run = await RunScenarioAsync(scenario, scenario.AgentName, agentProfile: null, toolManager, mode, ct);
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
        IToolManager? toolManager)
    {
        if (seedScenario != null) await seedScenario(scenario, ct);
        AgentRun run = await RunScenarioAsync(scenario, agentName, agentProfile, toolManager, mode, ct);
        IReadOnlyList<InvariantResult> invariants = StructuralScorer.Score(run.Trace, scenario, checkers);
        QualityScore? quality = scenario.Rubric.Count > 0
            ? await _judge.JudgeAsync(run.Trace, agentDefinition, scenario, invariants, ct)
            : null;
        EfficiencyMetrics efficiency = EfficiencyExtractor.Extract(run.Trace, run.LatencyMs);
        ScoreCard card = ScoreCardBuilder.Build(scenario, run.Trace, invariants, quality, efficiency, sampleIndex, mode);
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
    /// <see cref="ReplayInference.BuildModel(string, IReadOnlyList{ReplayTurn})"/> and threaded through the
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
        IToolManager? toolManager = null)
        => ScoreSetCoreAsync(scenarios, agentName, agentProfile: null, agentDefinition, checkers, mode, samples, governor, ct, seedScenario, toolManager);

    /// <summary>
    /// Candidate overload of <see cref="ScoreSetAsync(IReadOnlyList{Scenario}, string, string, IReadOnlyList{IInvariantChecker}, string, int, BudgetGovernor, CancellationToken, Func{Scenario, CancellationToken, Task}?, IToolManager?)"/>:
    /// scores the whole set by running the parsed candidate <paramref name="agentProfile"/> DIRECTLY (via the
    /// per-run profile overload, no shared registry slot), carrying the per-run <paramref name="toolManager"/>.
    /// </summary>
    private Task<(List<ScoreCard> Cards, Dictionary<string, string> Outputs)> ScoreSetAsync(
        IReadOnlyList<Scenario> scenarios,
        AgentProfile agentProfile,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        string mode,
        int samples,
        BudgetGovernor governor,
        CancellationToken ct,
        Func<Scenario, CancellationToken, Task>? seedScenario,
        IToolManager? toolManager = null)
        => ScoreSetCoreAsync(scenarios, agentProfile.Name ?? "", agentProfile, agentDefinition, checkers, mode, samples, governor, ct, seedScenario, toolManager);

    /// <summary>Shared body for both <see cref="ScoreSetAsync"/> overloads.</summary>
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
        IToolManager? toolManager)
    {
        List<ScoreCard> cards = [];
        Dictionary<string, string> outputs = [];

        foreach (Scenario scenario in scenarios)
        {
            for (int sample = 0; sample < samples; sample++)
            {
                ct.ThrowIfCancellationRequested();
                (ScoreCard card, string output) =
                    await RunAndScoreAsync(scenario, agentName, agentProfile, agentDefinition, checkers, mode, sample, ct, seedScenario, toolManager);
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
            Id = Guid.NewGuid().ToString("n"),
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
