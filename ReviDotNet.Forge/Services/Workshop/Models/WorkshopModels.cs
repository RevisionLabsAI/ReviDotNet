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
