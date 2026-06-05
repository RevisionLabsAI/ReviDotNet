// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using ReviDotNet.Forge.Services.Workshop.Models;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// Process-wide registry of Workshop run "instances" and "evaluations". Singleton so the
/// lists survive page navigation and component remounts within a browser session — the
/// durable trace/output data itself lives in ReviLog (per session) and the agent version
/// history lives on disk; this store only holds the grouping metadata the UI lists.
/// </summary>
public interface IWorkshopStore
{
    /// <summary>Raised whenever an instance or evaluation is added or mutated.</summary>
    event Action? Changed;

    // ── Instances ───────────────────────────────────────────────────────────
    /// <summary>All instances for an agent, newest first.</summary>
    IReadOnlyList<WorkshopInstance> GetInstances(string agentName);
    WorkshopInstance? GetInstance(string id);
    void AddInstance(WorkshopInstance instance);

    // ── Evaluations ─────────────────────────────────────────────────────────
    /// <summary>All evaluations for an agent, newest first.</summary>
    IReadOnlyList<WorkshopEvaluation> GetEvaluations(string agentName);
    WorkshopEvaluation? GetEvaluation(string id);
    void AddEvaluation(WorkshopEvaluation evaluation);

    /// <summary>
    /// Signals that a previously-added instance/evaluation was mutated in place
    /// (sessions appended, stats/revision filled in) so listeners can re-render.
    /// </summary>
    void NotifyChanged();
}
