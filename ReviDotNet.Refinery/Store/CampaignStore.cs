// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace Revi.Refinery;

/// <summary>
/// Persistence for campaigns and the append-only experiment ledger. The in-memory implementation is the
/// default; a durable (Mongo) implementation can be added later for history without changing callers.
/// </summary>
public interface ICampaignStore
{
    /// <summary>Insert or update a campaign.</summary>
    Task SaveAsync(Campaign campaign, CancellationToken ct = default);

    /// <summary>Get a campaign by id, or null.</summary>
    Task<Campaign?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>List all campaigns.</summary>
    Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default);

    /// <summary>Append a ledger entry for a campaign.</summary>
    Task AppendLedgerAsync(LedgerEntry entry, CancellationToken ct = default);

    /// <summary>Get a campaign's ledger entries.</summary>
    Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default);
}

/// <summary>In-memory <see cref="ICampaignStore"/> (default; campaigns are lost on restart).</summary>
public sealed class InMemoryCampaignStore : ICampaignStore
{
    private readonly ConcurrentDictionary<string, Campaign> _campaigns = new();
    private readonly ConcurrentDictionary<string, List<LedgerEntry>> _ledger = new();

    /// <inheritdoc/>
    public Task SaveAsync(Campaign campaign, CancellationToken ct = default)
    {
        _campaigns[campaign.Id] = campaign;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Campaign?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_campaigns.GetValueOrDefault(id));

    /// <inheritdoc/>
    public Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Campaign>>(_campaigns.Values.ToList());

    /// <inheritdoc/>
    public Task AppendLedgerAsync(LedgerEntry entry, CancellationToken ct = default)
    {
        _ledger.GetOrAdd(entry.CampaignId, _ => []).Add(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LedgerEntry>>(_ledger.GetValueOrDefault(campaignId)?.ToList() ?? []);
}
