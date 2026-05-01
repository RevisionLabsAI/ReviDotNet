// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Services.Observer;

public sealed record ReviLogQuery(
    string? InstanceId,
    DateTime? From,
    DateTime? To,
    IReadOnlySet<Revi.LogLevel>? Levels,
    string? Search,
    bool Descending = true,
    int PageSize = 50,
    int Page = 0,
    string? AgentName = null,
    string? AgentSessionId = null,
    int? AgentMaxDepth = null
);

public sealed record AgentNameDto(string Name, long EventCount);

public sealed record AgentSessionDto(
    string SessionId,
    string AgentName,
    DateTime StartedAt,
    DateTime LastSeenAt,
    long EventCount,
    string? RootEventId
);

public sealed record ReviInstanceDto(
    string InstanceId,
    DateTime StartedAt,
    DateTime LastSeenAt,
    long EventCount
);

public interface IReviLogViewerService
{
    Task<IReadOnlyList<ReviInstanceDto>> GetInstancesAsync(int skip, int take, CancellationToken ct = default);
    Task<long> CountInstancesAsync(CancellationToken ct = default);
    Task<long> CountLogsAsync(ReviLogQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<RlogEvent>> GetLogsPageAsync(ReviLogQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<RlogEvent>> GetChildrenAsync(IReadOnlyCollection<string> parentIds, CancellationToken ct = default);
    Task<long> ClearInstanceLogsAsync(string instanceId, DateTime? olderThanUtc = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct list of agent names seen in recent log events, ordered by recency/activity.
    /// Names are extracted from the agent:&lt;name&gt; token in the Tags field.
    /// </summary>
    Task<IReadOnlyList<AgentNameDto>> GetAgentNamesAsync(int take, CancellationToken ct = default);

    /// <summary>
    /// Returns recent agent run sessions (one per session id) for the given agent name, newest first.
    /// </summary>
    Task<IReadOnlyList<AgentSessionDto>> GetAgentSessionsAsync(string agentName, int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Recursively expands a session's full event subtree by walking ParentId. Cap is enforced
    /// to prevent runaway expansion (default 5000 events).
    /// </summary>
    Task<IReadOnlyList<RlogEvent>> GetSessionEventsAsync(string sessionId, int maxEvents = 5000, CancellationToken ct = default);
}
