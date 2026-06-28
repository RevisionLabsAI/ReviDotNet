// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace Revi.Refinery;

/// <summary>
/// An in-process <see cref="IRlogEventPublisher"/> that captures the ReviLog events emitted during agent
/// runs so the engine can project them into typed <see cref="AgentTrace"/>s — no MongoDB required.
/// <para>
/// Registered as the host process's publisher (with <c>ReviServiceLocator.SetProvider</c>) so
/// <c>AgentRunner</c>'s events flow here. A single shared buffer is drained per run, so runs through one
/// publisher instance must be serialized; concurrent campaigns should use separate scopes/instances.
/// </para>
/// </summary>
public sealed class CapturingRlogPublisher : IRlogEventPublisher
{
    private readonly ConcurrentQueue<RlogEvent> _events = new();

    /// <inheritdoc/>
    public Task PublishLogEventAsync(RlogEvent rlogEvent)
    {
        _events.Enqueue(rlogEvent);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void PublishLogEvent(RlogEvent rlogEvent) => _events.Enqueue(rlogEvent);

    /// <summary>Discard all buffered events (call before a run).</summary>
    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
    }

    /// <summary>A non-destructive snapshot of the current buffer.</summary>
    public IReadOnlyList<RlogEvent> Snapshot() => _events.ToArray();

    /// <summary>Remove and return all buffered events (call after a run).</summary>
    public IReadOnlyList<RlogEvent> Drain()
    {
        List<RlogEvent> list = [];
        while (_events.TryDequeue(out RlogEvent? e))
            list.Add(e);
        return list;
    }
}
