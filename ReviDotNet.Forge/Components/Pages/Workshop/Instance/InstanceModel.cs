// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace ReviDotNet.Forge.Components.Pages.Workshop.Instance;

/// <summary>
/// The view-model behind the "Grouped" agent-instance watching view (ported from the
/// <c>05-grouped</c> mockup). A run is a list of <see cref="ActivationView"/> — one per state
/// activation (cycle) — each holding the <see cref="StepView"/>s (LLM turns) that ran while the
/// agent was in that state, and each step holding its <see cref="CallView"/>s (tool calls and
/// spawned sub-agents). Sub-agents nest the same shape recursively via <see cref="CallView.Steps"/>.
///
/// This mirrors the real <c>RlogEvent</c> tree (run-root → llm-request → tool-call → …); a later
/// pass will project live runs into this shape. For now <see cref="DummyInstance"/> builds a
/// faithful demo trace so the visuals can be confirmed.
/// </summary>
public sealed class GroupedInstanceData
{
    public required InstanceMeta Meta { get; init; }
    public required IReadOnlyList<ActivationView> Activations { get; init; }

    /// <summary>Whole-run wall time = sum of every step's duration across all activations.</summary>
    public long TotalRunMs => Activations.Sum(a => a.DurationMs);
}

/// <summary>Status of a step, call, activation or the run itself — drives the pill + cube colour.</summary>
public enum CallStatus
{
    Done,
    Running,
    Failed,
    Queued,
    Dropped,
}

/// <summary>A call is either a plain tool invocation or a spawned sub-agent.</summary>
public enum CallKind
{
    Tool,
    SubAgent,
}

/// <summary>Header block: identity, the task, the four stat tiles and the budget/step meters.</summary>
public sealed class InstanceMeta
{
    public required string Agent { get; init; }
    public required string Version { get; init; }
    public required string Model { get; init; }
    public required string Session { get; init; }

    /// <summary>Run start, already formatted for display (e.g. <c>2:58:02 PM</c>).</summary>
    public required string Started { get; init; }

    /// <summary>Overall run status — selects the header pill colour.</summary>
    public required CallStatus Status { get; init; }

    /// <summary>Header pill text, e.g. <c>Running · Step 4</c> or the final <c>AgentExitReason</c>.</summary>
    public required string StatusText { get; init; }

    public required string Task { get; init; }
    public IReadOnlyList<StatTile> Tiles { get; init; } = [];
    public IReadOnlyList<MeterBar> Meters { get; init; } = [];
}

/// <summary>One of the four header stat tiles (icon + value + label, tinted by accent).</summary>
public sealed class StatTile
{
    /// <summary>Token name to tint with: <c>primary</c> / <c>secondary</c> / <c>tertiary</c> / <c>info</c>.</summary>
    public required string Accent { get; init; }
    public required string Icon { get; init; }
    public required string Value { get; init; }
    public required string Label { get; init; }
}

/// <summary>A labelled progress meter (cost budget, step budget, …) drawn under the tiles.</summary>
public sealed class MeterBar
{
    public required string Label { get; init; }
    public required string Value { get; init; }

    /// <summary>Fill percent 0–100; ≥80 turns the bar amber, ≥100 red.</summary>
    public required int Pct { get; init; }
}

/// <summary>
/// A single state activation (one trip through a state = one cycle). Groups the steps that ran
/// while the agent was in <see cref="State"/>, and records the signal that ended it.
/// </summary>
public sealed class ActivationView
{
    public required string State { get; init; }
    public required int Cycle { get; init; }
    public required CallStatus Status { get; init; }

    /// <summary>The end signal that closed this activation (READY / DONE / CONTINUE / ABORT), if any.</summary>
    public string? EndSignal { get; init; }

    /// <summary>The state the agent moved to after this activation, if any.</summary>
    public string? NextState { get; init; }

    public IReadOnlyList<StepView> Steps { get; init; } = [];

    /// <summary>A cycle's wall time = sum of its steps' durations.</summary>
    public long DurationMs => Steps.Sum(s => s.DurationMs ?? 0);
}

/// <summary>One LLM turn: its thinking, its assistant message, and the calls it dispatched.</summary>
public sealed class StepView
{
    /// <summary>Internal, run-global 0-based step index (display is 1-based: <c>Step {No + 1}</c>).</summary>
    public required int No { get; init; }

    public required CallStatus Status { get; init; }
    public long? DurationMs { get; init; }

    /// <summary>Whether the step starts expanded.</summary>
    public bool Open { get; init; }

    public required string Title { get; init; }
    public string? Thinking { get; init; }
    public string? Message { get; init; }

    public IReadOnlyList<CallView> Calls { get; init; } = [];

    /// <summary>1-based index of a call to pre-select when the calls render as a cube grid.</summary>
    public int? Select { get; init; }

    /// <summary>The <c>max-parallel-tools</c> cap in effect for this step, when relevant.</summary>
    public int? MaxParallel { get; init; }
}

/// <summary>A tool call or a spawned sub-agent dispatched within a step.</summary>
public sealed class CallView
{
    public required CallKind Kind { get; init; }

    /// <summary>Icon sprite id (e.g. <c>i-globe</c>, <c>i-search</c>, <c>i-sub</c>).</summary>
    public required string Icon { get; init; }

    public required string Name { get; init; }

    /// <summary>Sub-agent version tag (e.g. <c>v2</c>); null for plain tools.</summary>
    public string? Version { get; init; }

    public required CallStatus Status { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public long? DurationMs { get; init; }

    /// <summary>Tool input payload (rendered as code); null for sub-agents.</summary>
    public string? Input { get; init; }

    /// <summary>Tool output / error / not-executed note; null for sub-agents.</summary>
    public string? Output { get; init; }

    /// <summary>For a sub-agent: its own steps, numbered <c>parent.child</c> (e.g. Step 4.1).</summary>
    public IReadOnlyList<StepView>? Steps { get; init; }
}

/// <summary>Per-status call counts for the segmented badge on a multi-call step header.</summary>
public readonly record struct StatusBreakdown(int Done, int Running, int Failed, int Queued, int Dropped);

/// <summary>Display formatting shared across the grouped-view components.</summary>
public static class GroupedFormat
{
    /// <summary>Fine-grained duration: <c>900ms</c> under a second, else one-decimal seconds (<c>1.5s</c>).</summary>
    public static string Dur(long ms) => ms < 1000 ? $"{ms}ms" : $"{(ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s";

    /// <summary>
    /// Coarse "time ran" for cycles / steps / the whole run: whole seconds, switching to minutes
    /// past 60s — <c>59s → 1m → 1m 1s → 2m 18s</c> (and hours past 60m).
    /// </summary>
    public static string Ran(long ms)
    {
        long s = (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);
        if (s < 60) return $"{s}s";
        long m = s / 60, rs = s % 60;
        if (m < 60) return rs > 0 ? $"{m}m {rs}s" : $"{m}m";
        long h = m / 60, rm = m % 60;
        return rm > 0 ? $"{h}h {rm}m" : $"{h}h";
    }

    /// <summary>Lower-case status key used in CSS class suffixes (<c>step--done</c>, <c>cube done</c>).</summary>
    public static string StatusKey(CallStatus s) => s switch
    {
        CallStatus.Done => "done",
        CallStatus.Running => "running",
        CallStatus.Failed => "failed",
        CallStatus.Dropped => "dropped",
        _ => "queued",
    };

    /// <summary>The status-pill class for a given status.</summary>
    public static string StatusClass(CallStatus s) => s switch
    {
        CallStatus.Done => "st-ok",
        CallStatus.Failed => "st-fail",
        CallStatus.Running => "st-run",
        CallStatus.Dropped => "st-drop",
        _ => "st-wait",
    };

    /// <summary>The status-pill label; a done call shows its duration when known.</summary>
    public static string StatusLabel(CallStatus s, long? ms) => s switch
    {
        CallStatus.Done => ms is { } d ? Dur(d) : "Done",
        CallStatus.Failed => "Failed",
        CallStatus.Running => "Running",
        CallStatus.Dropped => "Dropped",
        _ => "Queued",
    };

    public static string KindKey(CallKind k) => k == CallKind.SubAgent ? "subagent" : "tool";

    /// <summary>Tally calls by status for the segmented badge.</summary>
    public static StatusBreakdown Breakdown(IEnumerable<CallView> calls)
    {
        int done = 0, running = 0, failed = 0, queued = 0, dropped = 0;
        foreach (var c in calls)
        {
            switch (c.Status)
            {
                case CallStatus.Done: done++; break;
                case CallStatus.Running: running++; break;
                case CallStatus.Failed: failed++; break;
                case CallStatus.Dropped: dropped++; break;
                default: queued++; break;
            }
        }
        return new StatusBreakdown(done, running, failed, queued, dropped);
    }
}
