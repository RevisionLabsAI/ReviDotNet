// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>Lifecycle status of a refinement campaign.</summary>
public enum CampaignStatus
{
    Pending,
    Running,
    Converged,
    BudgetExhausted,
    Stopped,
    Failed
}

/// <summary>The request that starts a refinement campaign.</summary>
public sealed record CampaignSpec
{
    /// <summary>The plugin owning the agent.</summary>
    public required string PluginName { get; init; }

    /// <summary>The agent to refine.</summary>
    public required string AgentName { get; init; }

    /// <summary>The scenario suite to evaluate against.</summary>
    public required string SuiteName { get; init; }

    /// <summary>Samples per scenario (for variance).</summary>
    public int SamplesPerScenario { get; init; } = 3;

    /// <summary>"live" (real LLM + tools) or "replay" (scripted).</summary>
    public string Mode { get; init; } = "live";

    /// <summary>Token budget for the whole campaign; null = bounded only by <see cref="MaxRounds"/>.</summary>
    public long? TokenBudget { get; init; }

    /// <summary>
    /// Separate token budget for meta-LLM calls (judge, gate, proposer); null = no dedicated meta cap.
    /// Tracked independently of the agent-run <see cref="TokenBudget"/> so meta spend can be bounded on its own.
    /// </summary>
    public long? MetaTokenBudget { get; init; }

    /// <summary>Maximum improvement rounds.</summary>
    public int MaxRounds { get; init; } = 10;

    /// <summary>Stop after this many consecutive rounds with no accepted improvement.</summary>
    public int StopAfterNoImprovementRounds { get; init; } = 2;

    /// <summary>When false, only measure a baseline (no proposal/refinement).</summary>
    public bool AutoPropose { get; init; } = true;
}

/// <summary>Aggregate scores for a set of runs (one agent variant over a suite).</summary>
public sealed record SuiteAggregate
{
    /// <summary>Fraction of runs with zero invariant violations.</summary>
    public double InvariantPassRate { get; init; }

    /// <summary>Mean overall quality (1–10).</summary>
    public double QualityMean { get; init; }

    /// <summary>10th-percentile quality (worst-case the user might see).</summary>
    public double QualityP10 { get; init; }

    /// <summary>Mean USD cost per run.</summary>
    public decimal CostMean { get; init; }

    /// <summary>90th-percentile latency.</summary>
    public long LatencyP90Ms { get; init; }

    /// <summary>Number of runs aggregated.</summary>
    public int RunCount { get; init; }

    /// <summary>Runs that actually received a judge quality score.</summary>
    public int QualityScoredRuns { get; init; }

    /// <summary>
    /// Runs that expected a quality score (their scenario had a rubric) but got none — i.e. the judge
    /// call failed or its verdict didn't parse. Non-zero here means <see cref="QualityMean"/> /
    /// <see cref="QualityP10"/> are computed over fewer runs than were evaluated: a broken judge must
    /// look like a broken judge, not like "quality = 0".
    /// </summary>
    public int QualityJudgeFailures { get; init; }

    /// <summary>
    /// How many of the runs actually had at least one invariant evaluated. When 0, the structural
    /// <see cref="InvariantPassRate"/> is meaningless (nothing was gated) — surface this so a suite that
    /// checks nothing can't masquerade as 100% passing.
    /// </summary>
    public int GatedRunCount { get; init; }

    /// <summary>Per-invariant pass rate.</summary>
    public IReadOnlyDictionary<string, double> InvariantPassRateById { get; init; } = new Dictionary<string, double>();
}

/// <summary>A candidate agent variant produced during a campaign.</summary>
public sealed record VariantRecord
{
    public required string Id { get; init; }
    public required string AgentName { get; init; }
    public required int Round { get; init; }

    /// <summary>The knob class changed: system-prompt | state-instruction | few-shot | sampling | guardrail | state-graph | model | tool-gating.</summary>
    public string KnobType { get; init; } = "";

    /// <summary>The proposed unified diff of the .agent/.pmt change.</summary>
    public string Diff { get; init; } = "";

    /// <summary>The full revised .agent/.pmt content — written to disk when an accepted variant is promoted.</summary>
    public string RevisedContent { get; init; } = "";

    public SuiteAggregate? TrainScores { get; init; }
    public SuiteAggregate? HeldOutScores { get; init; }

    /// <summary>Null = not yet decided.</summary>
    public bool? Accepted { get; init; }

    /// <summary>Why it was accepted/rejected.</summary>
    public string? Decision { get; init; }
}

/// <summary>One round of a campaign.</summary>
public sealed record CampaignIteration
{
    public required int Round { get; init; }
    public SuiteAggregate? Baseline { get; init; }
    public IReadOnlyList<VariantRecord> Variants { get; init; } = [];
    public string? AcceptedVariantId { get; init; }
}

/// <summary>The full state of a refinement campaign — the dashboard/CLI view-model and history record.</summary>
public sealed record Campaign
{
    public required string Id { get; init; }
    public required CampaignSpec Spec { get; init; }
    public CampaignStatus Status { get; init; } = CampaignStatus.Pending;
    public SuiteAggregate? Baseline { get; init; }
    public SuiteAggregate? Current { get; init; }
    public IReadOnlyList<CampaignIteration> Iterations { get; init; } = [];

    /// <summary>Agent-execution tokens spent so far (updated as runs complete, not just at round ends).</summary>
    public long TokensSpent { get; init; }

    /// <summary>Meta-LLM tokens (judge / pairwise / proposer) spent so far — the other half of the bill.</summary>
    public long MetaTokensSpent { get; init; }

    public string? Error { get; init; }
}

/// <summary>An append-only ledger entry recording one variant attempt and its outcome.</summary>
public sealed record LedgerEntry
{
    public required string CampaignId { get; init; }
    public required int Round { get; init; }
    public required string AgentName { get; init; }
    public string KnobType { get; init; } = "";
    public string Diff { get; init; } = "";
    public SuiteAggregate? TrainScores { get; init; }
    public SuiteAggregate? HeldOutScores { get; init; }
    public bool Accepted { get; init; }
    public string? RejectReason { get; init; }
    public long TokensSpent { get; init; }
}
