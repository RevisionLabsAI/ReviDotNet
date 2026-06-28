// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace Revi.Refinery;

/// <summary>A per-run capture buffer of the ReviLog events emitted during one agent run.</summary>
public sealed class RunCapture
{
    private readonly ConcurrentQueue<RlogEvent> _events = new();

    internal void Add(RlogEvent e) => _events.Enqueue(e);

    /// <summary>The captured events (time order is preserved by the builder).</summary>
    public IReadOnlyList<RlogEvent> Events => _events.ToArray();
}

/// <summary>
/// Routes agent-run ReviLog events to the <i>current async flow's</i> capture buffer, so a campaign run's
/// trace is isolated from other agent activity in the same process: concurrent runs live in different
/// async contexts (separate buffers), while a run's own sub-agents share its context (same buffer).
/// The host registers the <see cref="CompositeRlogPublisher"/> as its publisher; the engine opens a
/// capture scope around each run.
/// </summary>
public sealed class RefineryCaptureBroker
{
    private static readonly AsyncLocal<RunCapture?> Current = new();

    /// <summary>Begin capturing into a fresh buffer for the current async flow; dispose to stop.</summary>
    public CaptureScope BeginCapture()
    {
        RunCapture capture = new();
        Current.Value = capture;
        return new CaptureScope(capture, this);
    }

    internal void Receive(RlogEvent e) => Current.Value?.Add(e);

    private void End() => Current.Value = null;

    /// <summary>A scoped capture; read <see cref="Capture"/>, and disposal stops routing to it.</summary>
    public sealed class CaptureScope(RunCapture capture, RefineryCaptureBroker broker) : IDisposable
    {
        /// <summary>The buffer events for this run are captured into.</summary>
        public RunCapture Capture { get; } = capture;

        /// <inheritdoc/>
        public void Dispose() => broker.End();
    }
}

/// <summary>
/// An <see cref="IRlogEventPublisher"/> that forwards every event to an inner publisher (the host's — e.g.
/// Forge's broadcasting publisher that drives its live UI) AND to the Refinery capture broker. Trace
/// capture is therefore <b>additive</b> and never displaces the host's logging.
/// </summary>
public sealed class CompositeRlogPublisher(IRlogEventPublisher inner, RefineryCaptureBroker broker) : IRlogEventPublisher
{
    /// <inheritdoc/>
    public Task PublishLogEventAsync(RlogEvent rlogEvent)
    {
        broker.Receive(rlogEvent);
        return inner.PublishLogEventAsync(rlogEvent);
    }

    /// <inheritdoc/>
    public void PublishLogEvent(RlogEvent rlogEvent)
    {
        broker.Receive(rlogEvent);
        inner.PublishLogEvent(rlogEvent);
    }
}
