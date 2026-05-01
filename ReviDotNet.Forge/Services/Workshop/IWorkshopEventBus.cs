// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// In-memory pub/sub for ReviLog events tagged with an agent-session id.
/// Lets the Workshop UI receive live updates without polling MongoDB.
///
/// Subscriptions are by session id (extracted from the agent-session:&lt;guid&gt; tag).
/// A null session id receives every event.
/// </summary>
public interface IWorkshopEventBus
{
    void Publish(RlogEvent ev);
    IDisposable Subscribe(string sessionId, Action<RlogEvent> callback);
}
