// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;
using ReviDotNet.Forge.Services.Workshop.Models;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// Backend for the Agent Workshop UI. Drives multi-run agent execution, fetches
/// hierarchical session traces from ReviLog, and orchestrates LLM-driven
/// evaluation + diff generation against the source .agent file.
/// </summary>
public interface IAgentWorkshopService
{
    /// <summary>
    /// Runs the chosen agent N times in parallel against the same task. Yields per-run
    /// updates as ReviLog events are produced and final AgentResult on completion.
    /// </summary>
    IAsyncEnumerable<WorkshopRunUpdate> RunMultiAsync(WorkshopRunRequest request, CancellationToken ct);

    /// <summary>Returns all events for one session, recursively expanded (includes sub-agents).</summary>
    Task<IReadOnlyList<RlogEvent>> GetSessionEventsAsync(string sessionId, CancellationToken ct);

    /// <summary>Lists recent sessions for an agent — paginated.</summary>
    Task<IReadOnlyList<AgentSessionSummary>> GetSessionsForAgentAsync(string agentName, int skip, int take, CancellationToken ct);

    /// <summary>
    /// Calls the AgentWorkshop.Evaluator prompt with the given session(s) and returns
    /// a structured evaluation including ranked recommendations and alternatives.
    /// </summary>
    Task<AgentEvaluationResult?> EvaluateSessionsAsync(string agentName, IReadOnlyList<string> sessionIds, CancellationToken ct);

    /// <summary>
    /// Streams a proposed full .agent file revision from AgentWorkshop.Reviser based on
    /// the chosen recommendation. The current agent's text is read from disk if available.
    /// </summary>
    IAsyncEnumerable<string> GenerateAgentDiffAsync(string agentName, AgentRecommendation recommendation, AgentEvaluationResult evaluation, CancellationToken ct);

    /// <summary>
    /// Persists a revised .agent file to disk and reloads AgentManager so subsequent
    /// runs pick up the new revision. Throws if the agent's source file cannot be located.
    /// </summary>
    Task SaveAgentRevisionAsync(string agentName, string newContent, CancellationToken ct);

    /// <summary>Returns the raw .agent file text for the given registered agent, or null if not on disk.</summary>
    Task<string?> ReadAgentSourceAsync(string agentName, CancellationToken ct);
}
