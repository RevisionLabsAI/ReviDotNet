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
    private readonly ConcurrentDictionary<string, WorkshopInstance> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkshopEvaluation> _evaluations = new(StringComparer.Ordinal);

    public event Action? Changed;

    // ── Instances ───────────────────────────────────────────────────────────

    public IReadOnlyList<WorkshopInstance> GetInstances(string agentName)
        => _instances.Values
            .Where(i => string.Equals(i.AgentName, agentName, StringComparison.Ordinal))
            .OrderByDescending(i => i.CreatedAt)
            .ToList();

    public WorkshopInstance? GetInstance(string id)
        => _instances.TryGetValue(id, out var i) ? i : null;

    public void AddInstance(WorkshopInstance instance)
    {
        _instances[instance.Id] = instance;
        Changed?.Invoke();
    }

    // ── Evaluations ─────────────────────────────────────────────────────────

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
