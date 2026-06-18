// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using ReviDotNet.Forge.Services.Workshop.Models;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// Default in-memory implementation of <see cref="IWorkshopStore"/>. Instances and
/// evaluations are kept in concurrent dictionaries keyed by their id and filtered by
/// agent on read. Entries are added once and then mutated in place (callers append
/// session ids and fill stats/revisions), so the store holds references and a single
/// <see cref="Changed"/> event drives UI refreshes.
/// </summary>
public sealed class WorkshopStore : IWorkshopStore
{
    private readonly ConcurrentDictionary<string, WorkshopSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkshopEvaluation> _evaluations = new(StringComparer.Ordinal);

    public event Action? Changed;

    // ── Sessions ────────────────────────────────────────────────────────────

    public IReadOnlyList<WorkshopSession> GetAllSessions()
        => _sessions.Values
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

    public IReadOnlyList<WorkshopSession> GetSessions(string agentName)
        => _sessions.Values
            .Where(s => string.Equals(s.AgentName, agentName, StringComparison.Ordinal))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

    public WorkshopSession? GetSession(string id)
        => _sessions.TryGetValue(id, out var s) ? s : null;

    public void AddSession(WorkshopSession session)
    {
        _sessions[session.Id] = session;
        Changed?.Invoke();
    }

    // ── Evaluations ─────────────────────────────────────────────────────────

    public IReadOnlyList<WorkshopEvaluation> GetAllEvaluations()
        => _evaluations.Values
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

    public IReadOnlyList<WorkshopEvaluation> GetEvaluations(string agentName)
        => _evaluations.Values
            .Where(e => string.Equals(e.AgentName, agentName, StringComparison.Ordinal))
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

    public WorkshopEvaluation? GetEvaluation(string id)
        => _evaluations.TryGetValue(id, out var e) ? e : null;

    public void AddEvaluation(WorkshopEvaluation evaluation)
    {
        _evaluations[evaluation.Id] = evaluation;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();
}
