// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Optional context threaded into AgentRunner so a sub-agent run can nest its
/// ReviLog event tree under a parent run's tool-call event.
///
/// Top-level Agent.Run calls receive a fresh context (parent log = null, depth = 0).
/// InvokeAgentTool builds a child context from the ambient parent log so the
/// sub-agent's events nest correctly via Rlog.Parent / RlogEvent.ParentId.
/// </summary>
public sealed class AgentRunContext
{
    /// <summary>
    /// Async-local holder so InvokeAgentTool (dispatched inside Task.WhenAll) can
    /// read the per-tool-call parent context without a parameter on IBuiltInTool.
    /// AgentRunner pushes a child context immediately before each tool dispatch and
    /// pops it when the dispatch completes.
    /// </summary>
    private static readonly AsyncLocal<AgentRunContext?> _current = new();

    /// <summary>The context active for the current tool call, or null at top level.</summary>
    public static AgentRunContext? Current => _current.Value;

    /// <summary>
    /// The parent Rlog this run's root event should attach to. Null for top-level runs.
    /// Typically the tool-call Rlog from the parent agent that invoked this sub-agent.
    /// </summary>
    public Rlog? ParentLog { get; init; }

    /// <summary>
    /// Sub-agent nesting depth (0 = top-level, increments per InvokeAgentTool hop).
    /// Bounded by AgentGuardrails.MaxAgentDepth.
    /// </summary>
    public int Depth { get; init; } = 0;

    /// <summary>
    /// The maximum sub-agent depth permitted by the state that dispatched the current tool call,
    /// from <c>[[state.X.guardrails]] max-agent-depth</c>. Null means use the runner-wide default
    /// (<see cref="AgentRunner.DefaultMaxAgentDepth"/>). Set by AgentRunner immediately before each
    /// tool dispatch so InvokeAgentTool can enforce the active state's override.
    /// </summary>
    public int? MaxAgentDepthOverride { get; init; }

    /// <summary>
    /// Files attached to this run, if any. Threaded so the <c>list-files</c> / <c>read-file</c> tools
    /// can reach the attachments via <see cref="Current"/>. Propagated to sub-agent child contexts so
    /// nested agents can read the same files.
    /// </summary>
    public SessionFileRegistry? Files { get; init; }

    /// <summary>
    /// Convenience factory for a top-level (root) run context, optionally carrying attached files.
    /// </summary>
    public static AgentRunContext Root(SessionFileRegistry? files = null) => new() { Files = files };

    /// <summary>
    /// Builds a child context that nests under the given parent Rlog at depth + 1.
    /// </summary>
    /// <param name="parentLog">The parent Rlog the child run should attach to.</param>
    /// <param name="maxAgentDepthOverride">The dispatching state's max-agent-depth guardrail, if any.</param>
    public AgentRunContext Child(Rlog parentLog, int? maxAgentDepthOverride = null) =>
        new() { ParentLog = parentLog, Depth = Depth + 1, MaxAgentDepthOverride = maxAgentDepthOverride, Files = Files };

    /// <summary>
    /// Sets the ambient context for the duration of the returned scope. AgentRunner
    /// uses this around each tool dispatch so InvokeAgentTool can recover the parent
    /// log without modifying the IBuiltInTool interface.
    /// </summary>
    public static IDisposable Push(AgentRunContext ctx)
    {
        AgentRunContext? prev = _current.Value;
        _current.Value = ctx;
        return new PopScope(prev);
    }

    private sealed class PopScope : IDisposable
    {
        private readonly AgentRunContext? _prev;
        public PopScope(AgentRunContext? prev) { _prev = prev; }
        public void Dispose() => _current.Value = _prev;
    }
}
