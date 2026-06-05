// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Services.Workshop.Models;

/// <summary>
/// A live update emitted while a workshop run is executing. Either an event
/// from the agent's ReviLog tree, or the final AgentResult, or both.
/// </summary>
public sealed class WorkshopRunUpdate
{
    public required int RunIndex { get; init; }
    public required string SessionId { get; init; }
    public RlogEvent? Event { get; init; }
    public AgentResult? FinalResult { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Summary projection of one agent run session for History/list views.
/// </summary>
public sealed class AgentSessionSummary
{
    public required string SessionId { get; init; }
    public required string AgentName { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime LastSeenAt { get; init; }
    public required long EventCount { get; init; }
    public AgentExitReason? ExitReason { get; init; }
    public string? FinalOutputPreview { get; init; }
    public TimeSpan Duration => LastSeenAt - StartedAt;
}

/// <summary>
/// One LLM-generated recommendation (or alternative) for improving an agent.
/// </summary>
public sealed class AgentRecommendation
{
    public required string Title { get; init; }
    public required string Rationale { get; init; }
    public required string Impact { get; init; }
    public required RecommendationKind Kind { get; init; }
}

public enum RecommendationKind { Recommendation, Alternative }

/// <summary>
/// Aggregate stats across one batch of runs (1..N runs of the same agent on the same task).
/// </summary>
public sealed class AggregateStats
{
    public required int TotalRuns { get; init; }
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required double SuccessRate { get; init; }
    public required double AverageDurationSeconds { get; init; }
    public required double AverageSteps { get; init; }
}

/// <summary>
/// Full evaluation produced by AgentWorkshop.Evaluator over one or more runs.
/// </summary>
public sealed class AgentEvaluationResult
{
    public required string Verdict { get; init; }
    public required double Score { get; init; }
    public required double SuccessRate { get; init; }
    public required List<string> Strengths { get; init; }
    public required List<string> Weaknesses { get; init; }
    public required List<AgentRecommendation> Recommendations { get; init; }
    public required List<AgentRecommendation> Alternatives { get; init; }
    public AggregateStats? Stats { get; init; }
}

/// <summary>
/// Inputs sent to the workshop run trigger from the UI.
/// </summary>
public sealed class WorkshopRunRequest
{
    public required string AgentName { get; init; }
    public required string Task { get; init; }
    public Dictionary<string, object>? AdditionalInputs { get; init; }
    public string? ModelOverride { get; init; }
    public int Runs { get; init; } = 1;
}

/// <summary>
/// Parameters collected by the "New Instance" composer dialog and handed to the
/// Instances hub to create + run a <see cref="WorkshopInstance"/>.
/// </summary>
public sealed class NewInstanceSpec
{
    public string AgentName { get; set; } = "";
    public string Task { get; set; } = "";
    public int Runs { get; set; } = 1;
    public Dictionary<string, string> Inputs { get; set; } = new();
}

/// <summary>
/// A persisted run "instance": one task executed against one agent, possibly across
/// N parallel runs. The durable trace/output for each run lives in ReviLog (keyed by
/// <see cref="SessionIds"/>); this object is the grouping metadata the Workshop UI lists.
/// Mutable because <see cref="SessionIds"/> grows and <see cref="Stats"/> is filled in
/// as the runs stream and complete.
/// </summary>
public sealed class WorkshopInstance
{
    public required string Id { get; init; }
    public required string AgentName { get; init; }
    public int? AgentVersion { get; init; }
    public required string Task { get; init; }
    public Dictionary<string, object>? AdditionalInputs { get; init; }
    public int RunCount { get; init; } = 1;
    public List<string> SessionIds { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Aggregate outcome across the runs; null until at least one run completes.</summary>
    public AggregateStats? Stats { get; set; }

    /// <summary>True once execution has finished (successfully or not) and stats are final.</summary>
    public bool IsComplete { get; set; }
}

/// <summary>How a new evaluation sources the runs it will assess.</summary>
public enum EvaluationMode
{
    /// <summary>Assess the runs of an existing <see cref="WorkshopInstance"/>.</summary>
    ExistingInstance,
    /// <summary>Run the agent fresh on a task, then assess those runs.</summary>
    RunFresh
}

/// <summary>
/// Parameters collected by the "New Evaluation" composer dialog. The Evaluations hub
/// either evaluates an existing instance's sessions or runs the agent fresh first.
/// </summary>
public sealed class NewEvaluationSpec
{
    public EvaluationMode Mode { get; set; } = EvaluationMode.RunFresh;
    public string AgentName { get; set; } = "";

    /// <summary>Set when <see cref="Mode"/> is <see cref="EvaluationMode.ExistingInstance"/>.</summary>
    public string? InstanceId { get; set; }

    // RunFresh parameters:
    public string Task { get; set; } = "";
    public int Runs { get; set; } = 1;
    public Dictionary<string, string> Inputs { get; set; } = new();

    /// <summary>When true, auto-generate a revision proposal from the top recommendation.</summary>
    public bool AutoSuggest { get; set; } = true;
}

/// <summary>
/// A persisted evaluation: the LLM assessment of one or more run sessions for an agent,
/// plus the optional revision proposal generated from its top recommendation. Created by
/// the Evaluations hub either over an existing <see cref="WorkshopInstance"/> or from a
/// fresh run-and-evaluate.
/// </summary>
public sealed class WorkshopEvaluation
{
    public required string Id { get; init; }
    public required string AgentName { get; init; }
    public int? AgentVersion { get; init; }

    /// <summary>The instance this evaluation assessed, if it originated from one.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Human-readable task the evaluated runs were given (for list display).</summary>
    public string? Task { get; init; }

    public List<string> SessionIds { get; init; } = new();
    public required AgentEvaluationResult Result { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>The recommendation the proposed revision was generated from, if any.</summary>
    public AgentRecommendation? ChosenRecommendation { get; set; }

    /// <summary>Streamed full-file .agent revision proposal; null until generated.</summary>
    public string? ProposedRevision { get; set; }

    /// <summary>If the revision was approved and saved, the agent version it produced.</summary>
    public int? AppliedAsVersion { get; set; }
}
