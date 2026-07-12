// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>Overall verdict for a scored run. Any failed invariant ⇒ Fail, regardless of quality.</summary>
public enum RunVerdict
{
    Pass,
    Fail
}

/// <summary>A 1–10 quality facet score with rationale.</summary>
public sealed record FacetScore(string Name, int Score, string Rationale);

/// <summary>LLM-judge quality assessment of a run.</summary>
public sealed record QualityScore
{
    /// <summary>Overall 1–10 quality score.</summary>
    public int Overall { get; init; }

    /// <summary>Per-facet 1–10 scores.</summary>
    public IReadOnlyList<FacetScore> Facets { get; init; } = [];

    /// <summary>The judge's overall rationale.</summary>
    public string Rationale { get; init; } = "";

    /// <summary>The judge's self-reported confidence (1–5).</summary>
    public int JudgeConfidence { get; init; }
}

/// <summary>Cost/efficiency metrics extracted from a run's trace.</summary>
public sealed record EfficiencyMetrics
{
    public int TotalSteps { get; init; }
    public int ToolCalls { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public long LatencyMs { get; init; }
}

/// <summary>
/// A fact-checker agent's verdict for a run: the chosen winner, a 1–5 self-reported confidence, and an
/// optional rationale. Parsed from the run's final output when present; used by calibration analysis to
/// compare stated confidence against actual accuracy.
/// </summary>
public sealed record FactCheckerDetermination
{
    /// <summary>The winner the fact-checker selected.</summary>
    public string Winner { get; init; } = "";

    /// <summary>Self-reported confidence (1–5).</summary>
    public int Confidence { get; init; }

    /// <summary>Optional rationale for the determination.</summary>
    public string? Rationale { get; init; }
}

/// <summary>The combined score for one run of one scenario.</summary>
public sealed record ScoreCard
{
    /// <summary>The scenario this run scored.</summary>
    public required string ScenarioId { get; init; }

    /// <summary>The agent that ran.</summary>
    public required string AgentName { get; init; }

    /// <summary>The agent version/variant id, if known.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>"live" or "replay".</summary>
    public string Mode { get; init; } = "live";

    /// <summary>Which of the N samples this is.</summary>
    public int SampleIndex { get; init; }

    /// <summary>The run's exit reason.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>Per-invariant results (hard gates).</summary>
    public IReadOnlyList<InvariantResult> Invariants { get; init; } = [];

    /// <summary>Quality assessment (null if not judged).</summary>
    public QualityScore? Quality { get; init; }

    /// <summary>
    /// True when this run's scenario carried a quality rubric, i.e. a judge score was expected.
    /// <see cref="Quality"/> null while this is true means the judge call failed or didn't parse —
    /// aggregation surfaces those as judge failures instead of silently shrinking the quality sample.
    /// </summary>
    public bool QualityExpected { get; init; }

    /// <summary>Efficiency metrics (null if not extracted).</summary>
    public EfficiencyMetrics? Efficiency { get; init; }

    /// <summary>The run's session id, for deep-linking to the full trace.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The fact-checker's determination for this run, parsed from the final output, when present.
    /// Null when the run produced no parseable determination (the common case for non-fact-checker agents).
    /// </summary>
    public FactCheckerDetermination? FactCheckerDetermination { get; init; }

    /// <summary>Whether at least one invariant was actually evaluated for this run.</summary>
    public bool Gated => Invariants.Count > 0;

    /// <summary>
    /// Fail if any invariant failed; else Pass. Note a run with no invariants (<see cref="Gated"/> = false)
    /// is vacuously <see cref="RunVerdict.Pass"/> — check <see cref="Gated"/> before trusting a pass.
    /// </summary>
    public RunVerdict Verdict => Invariants.All(i => i.Passed) ? RunVerdict.Pass : RunVerdict.Fail;
}
