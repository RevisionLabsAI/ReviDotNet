// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// IRlogEventPublisher that fans out to an inner publisher (e.g. the Mongo-backed one)
/// AND publishes each event to the in-memory IWorkshopEventBus so the Workshop UI can
/// receive live updates without polling.
///
/// If the inner publisher is null/no-op, events still flow through the bus — Workshop
/// works even when MongoDB isn't configured (events just won't persist).
/// </summary>
public sealed class BroadcastingRlogEventPublisher : IRlogEventPublisher
{
    private readonly IRlogEventPublisher _inner;
    private readonly IWorkshopEventBus _bus;

    public BroadcastingRlogEventPublisher(IRlogEventPublisher inner, IWorkshopEventBus bus)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public Task PublishLogEventAsync(RlogEvent rlogEvent)
    {
        try { _bus.Publish(rlogEvent); } catch { /* never break logging */ }
        return _inner.PublishLogEventAsync(rlogEvent);
    }

    public void PublishLogEvent(RlogEvent rlogEvent)
    {
        try { _bus.Publish(rlogEvent); } catch { /* never break logging */ }
        _inner.PublishLogEvent(rlogEvent);
    }
}
