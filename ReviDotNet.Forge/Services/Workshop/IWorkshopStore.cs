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
    /// <summary>Raised whenever a session or evaluation is added or mutated.</summary>
    event Action? Changed;

    // ── Sessions ────────────────────────────────────────────────────────────
    /// <summary>All sessions across every agent, newest first.</summary>
    IReadOnlyList<WorkshopSession> GetAllSessions();
    /// <summary>All sessions for one agent, newest first.</summary>
    IReadOnlyList<WorkshopSession> GetSessions(string agentName);
    WorkshopSession? GetSession(string id);
    void AddSession(WorkshopSession session);

    // ── Evaluations ─────────────────────────────────────────────────────────
    /// <summary>All evaluations across every agent, newest first.</summary>
    IReadOnlyList<WorkshopEvaluation> GetAllEvaluations();
    /// <summary>All evaluations for one agent, newest first.</summary>
    IReadOnlyList<WorkshopEvaluation> GetEvaluations(string agentName);
    WorkshopEvaluation? GetEvaluation(string id);
    void AddEvaluation(WorkshopEvaluation evaluation);

    /// <summary>
    /// Signals that a previously-added instance/evaluation was mutated in place
    /// (sessions appended, stats/revision filled in) so listeners can re-render.
    /// </summary>
    void NotifyChanged();
}
