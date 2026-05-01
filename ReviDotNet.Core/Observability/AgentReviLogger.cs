// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Centralized helper for writing structured agent-run events to ReviLog.
/// All events share a consistent tag block so they can be filtered by agent,
/// session, step type, state, cycle, and depth without schema changes.
///
/// Event hierarchy is encoded via Rlog.Parent / RlogEvent.ParentId.
/// Tags are used only for discovery/filtering — not for relationship walking.
/// </summary>
public static class AgentReviLogger
{
    /// <summary>Standard step types written in the agent-step:&lt;type&gt; tag.</summary>
    public static class Step
    {
        public const string Start = "start";
        public const string LlmRequest = "llm-request";
        public const string LlmResponse = "llm-response";
        public const string Thinking = "thinking";
        public const string ToolCall = "tool-call";
        public const string ToolResult = "tool-result";
        public const string StateTransition = "state-transition";
        public const string End = "end";
        public const string GuardrailViolation = "guardrail-violation";
        public const string Error = "error";
    }

    /// <summary>
    /// Writes the run-root event for an agent activation. Returns the Rlog
    /// to be used as the parent for all subsequent step events.
    /// </summary>
    public static Rlog LogStart(
        Rlog? parentLog,
        string agentName,
        string sessionId,
        int depth,
        string entryState,
        Dictionary<string, object> inputs,
        object? profileSummary)
    {
        string tags = BuildTags(
            agentName: agentName,
            sessionId: sessionId,
            stepType: Step.Start,
            stateName: entryState,
            cycle: 0,
            depth: depth);

        if (TryGetLogger(out IReviLogger? logger) && logger != null)
        {
            return parentLog == null
                ? logger.Log(null, LogLevel.Info, $"Agent '{agentName}' run started", "agent-run-start", 0, tags, inputs, "inputs", profileSummary, "profile")
                : logger.Log(parentLog, LogLevel.Info, $"Sub-agent '{agentName}' run started", "agent-run-start", 0, tags, inputs, "inputs", profileSummary, "profile");
        }

        // No logger configured: build a detached Rlog so AgentRunner still has a parent reference for nesting.
        return new Rlog(parentLog, LogLevel.Info, $"Agent '{agentName}' run started", "agent-run-start", 0, tags, inputs, "inputs", profileSummary, "profile");
    }

    /// <summary>
    /// Writes a child event under the agent's run-root with a standardized tag block.
    /// Returns the new Rlog so callers can use it as a parent for further nesting
    /// (e.g. tool-result nests under tool-call; sub-agent root nests under tool-call).
    /// </summary>
    public static Rlog LogStep(
        Rlog parent,
        string agentName,
        string sessionId,
        string stepType,
        string stateName,
        int cycle,
        int depth,
        string message,
        object? object1 = null,
        string? object1Name = null,
        object? object2 = null,
        string? object2Name = null,
        LogLevel level = LogLevel.Info)
    {
        string tags = BuildTags(agentName, sessionId, stepType, stateName, cycle, depth);

        if (TryGetLogger(out IReviLogger? logger) && logger != null)
        {
            return logger.Log(parent, level, message, identifier: stepType, cycle: cycle, tags: tags, object1: object1, object1Name: object1Name, object2: object2, object2Name: object2Name);
        }

        return new Rlog(parent, level, message, stepType, cycle, tags, object1, object1Name, object2, object2Name);
    }

    /// <summary>
    /// Builds the standardized tag string for an agent event. Tags are space-separated
    /// to match the existing convention in Rlog.cs (parsed via split on space/comma).
    /// </summary>
    private static string BuildTags(
        string agentName,
        string sessionId,
        string stepType,
        string stateName,
        int cycle,
        int depth)
    {
        return string.Join(' ',
            $"agent:{agentName}",
            $"agent-session:{sessionId}",
            $"agent-step:{stepType}",
            $"agent-state:{stateName}",
            $"agent-cycle:{cycle}",
            $"agent-depth:{depth}");
    }

    private static bool TryGetLogger(out IReviLogger? logger)
        => ReviServiceLocator.TryGetLogger(out logger);
}
