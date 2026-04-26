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
    int Page = 0
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
}
