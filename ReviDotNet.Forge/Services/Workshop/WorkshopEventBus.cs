// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Revi;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// Default in-memory implementation of IWorkshopEventBus. Subscribers are stored per
/// session id (extracted from the agent-session:&lt;guid&gt; tag). Cheap to publish to
/// when there are no subscribers — events just get dropped.
/// </summary>
public sealed class WorkshopEventBus : IWorkshopEventBus
{
    private static readonly Regex SessionTag = new(@"(?:^|\s)agent-session:([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, List<Action<RlogEvent>>> _subscribers = new();

    public void Publish(RlogEvent ev)
    {
        if (ev?.Tags is null) return;

        var match = SessionTag.Match(ev.Tags);
        if (!match.Success) return;

        string sessionId = match.Groups[1].Value;
        if (!_subscribers.TryGetValue(sessionId, out var list)) return;

        Action<RlogEvent>[] snapshot;
        lock (list)
            snapshot = list.ToArray();

        foreach (var cb in snapshot)
        {
            try { cb(ev); }
            catch { /* subscribers must not break the publisher */ }
        }
    }

    public IDisposable Subscribe(string sessionId, Action<RlogEvent> callback)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException(null, nameof(sessionId));
        if (callback is null) throw new ArgumentNullException(nameof(callback));

        var list = _subscribers.GetOrAdd(sessionId, _ => new List<Action<RlogEvent>>());
        lock (list) list.Add(callback);

        return new Subscription(this, sessionId, callback);
    }

    private void Unsubscribe(string sessionId, Action<RlogEvent> callback)
    {
        if (!_subscribers.TryGetValue(sessionId, out var list)) return;
        lock (list)
        {
            list.Remove(callback);
            if (list.Count == 0)
                _subscribers.TryRemove(sessionId, out _);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly WorkshopEventBus _bus;
        private readonly string _sessionId;
        private readonly Action<RlogEvent> _callback;
        private bool _disposed;

        public Subscription(WorkshopEventBus bus, string sessionId, Action<RlogEvent> callback)
        {
            _bus = bus;
            _sessionId = sessionId;
            _callback = callback;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bus.Unsubscribe(_sessionId, _callback);
        }
    }
}
