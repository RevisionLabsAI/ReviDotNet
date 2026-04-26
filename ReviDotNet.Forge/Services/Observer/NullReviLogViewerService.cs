// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Services.Observer;

/// <summary>
/// No-op implementation used when MongoDB is not configured.
/// </summary>
public sealed class NullReviLogViewerService : IReviLogViewerService
{
    public Task<IReadOnlyList<ReviInstanceDto>> GetInstancesAsync(int skip, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReviInstanceDto>>(Array.Empty<ReviInstanceDto>());

    public Task<long> CountInstancesAsync(CancellationToken ct = default) =>
        Task.FromResult(0L);

    public Task<long> CountLogsAsync(ReviLogQuery query, CancellationToken ct = default) =>
        Task.FromResult(0L);

    public Task<IReadOnlyList<RlogEvent>> GetLogsPageAsync(ReviLogQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RlogEvent>>(Array.Empty<RlogEvent>());

    public Task<IReadOnlyList<RlogEvent>> GetChildrenAsync(IReadOnlyCollection<string> parentIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RlogEvent>>(Array.Empty<RlogEvent>());

    public Task<long> ClearInstanceLogsAsync(string instanceId, DateTime? olderThanUtc = null, CancellationToken ct = default) =>
        Task.FromResult(0L);
}
