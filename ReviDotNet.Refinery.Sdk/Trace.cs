// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>Canonical event types emitted during an agent run (mirrors ReviDotNet's AgentReviLogger steps).</summary>
public static class TraceEventTypes
{
    public const string Start = "start";
    public const string LlmRequest = "llm-request";
    public const string LlmResponse = "llm-response";
    public const string Thinking = "thinking";
    public const string ToolCall = "tool-call";
    public const string ToolStart = "tool-start";
    public const string ToolResult = "tool-result";
    public const string ToolDropped = "tool-dropped";
    public const string Content = "content";
    public const string StateTransition = "state-transition";
    public const string GuardrailViolation = "guardrail-violation";
    public const string Error = "error";
    public const string End = "end";
}

/// <summary>One event in a captured agent run, projected from a ReviLog event into a host-agnostic shape.</summary>
public sealed record TraceEvent
{
    /// <summary>One of <see cref="TraceEventTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>The agent FSM state active when this event fired (if any).</summary>
    public string? State { get; init; }

    /// <summary>The state re-activation cycle (self-loop counter).</summary>
    public int Cycle { get; init; }

    /// <summary>Sub-agent depth (0 = root agent).</summary>
    public int Depth { get; init; }

    /// <summary>Primary JSON payload — semantics depend on <see cref="Type"/> (e.g. messages, tool input, final output).</summary>
    public string? Object1 { get; init; }

    /// <summary>Secondary JSON payload — semantics depend on <see cref="Type"/> (e.g. token meta, tool name, exit meta).</summary>
    public string? Object2 { get; init; }

    /// <summary>When the event fired.</summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// A typed, host-agnostic projection of a single agent run — the unit that invariant checkers and the
/// LLM judge read. Built by the engine from the captured ReviLog event tree.
/// </summary>
public sealed record AgentTrace
{
    /// <summary>The run's session id (correlates all its events).</summary>
    public required string SessionId { get; init; }

    /// <summary>The agent that ran.</summary>
    public required string AgentName { get; init; }

    /// <summary>The agent's final output (content of the last step before end).</summary>
    public string? FinalOutput { get; init; }

    /// <summary>Exit reason string (Completed, GuardrailViolation, LoopDetected, BudgetExceeded, Error, …).</summary>
    public string ExitReason { get; init; } = "";

    /// <summary>True when the run completed normally.</summary>
    public bool Completed => string.Equals(ExitReason, "Completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Total LLM steps taken.</summary>
    public int TotalSteps { get; init; }

    /// <summary>Total input tokens reported across the run.</summary>
    public int InputTokens { get; init; }

    /// <summary>Total output tokens reported across the run (including sub-agent runs).</summary>
    public int OutputTokens { get; init; }

    /// <summary>Total provider-reported USD cost for the run (0 when no cost rates are configured).</summary>
    public decimal CostUsd { get; init; }

    /// <summary>Ordered states visited (including loop repeats).</summary>
    public IReadOnlyList<string> StateHistory { get; init; } = [];

    /// <summary>All captured events, time-ordered.</summary>
    public IReadOnlyList<TraceEvent> Events { get; init; } = [];

    /// <summary>All tool-call events.</summary>
    public IEnumerable<TraceEvent> ToolCalls => Events.Where(e => e.Type == TraceEventTypes.ToolCall);

    /// <summary>
    /// Tool-call events for a given tool name, matched strictly: a tool-call event's Object2 is exactly the
    /// JSON-quoted tool name, so we compare the unquoted name case-insensitively. Strict matching avoids
    /// false positives where a tool's <i>arguments</i> happen to mention another tool's name.
    /// </summary>
    public IEnumerable<TraceEvent> ToolCallsNamed(string toolName)
    {
        string needle = toolName.Trim();
        return ToolCalls.Where(e =>
            string.Equals((e.Object2 ?? string.Empty).Trim().Trim('"'), needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a tool with the given name was ever called.</summary>
    public bool CalledTool(string toolName) => ToolCallsNamed(toolName).Any();
}
