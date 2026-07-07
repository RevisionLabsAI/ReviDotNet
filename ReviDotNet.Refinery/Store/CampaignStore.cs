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

/// <summary>
/// In-memory <see cref="ICampaignStore"/> (default; campaigns are lost on restart). Also implements
/// <see cref="IScoreCardSource"/> so per-run score cards + scenario ground truth are captured for calibration
/// and knob-effectiveness analysis within the process lifetime.
/// </summary>
public sealed class InMemoryCampaignStore : ICampaignStore, IScoreCardSource
{
    private readonly ConcurrentDictionary<string, Campaign> _campaigns = new();
    private readonly ConcurrentDictionary<string, List<LedgerEntry>> _ledger = new();
    private readonly List<ScoreCard> _scoreCards = [];
    private readonly ConcurrentDictionary<string, string> _groundTruth = new();

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
        // Lock the per-campaign list: a dashboard/API read (GetLedgerAsync) can snapshot it while a running
        // campaign appends, and List<T> is not safe for concurrent read+write.
        List<LedgerEntry> list = _ledger.GetOrAdd(entry.CampaignId, _ => []);
        lock (list) list.Add(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default)
    {
        if (_ledger.GetValueOrDefault(campaignId) is not { } list)
            return Task.FromResult<IReadOnlyList<LedgerEntry>>([]);
        lock (list) return Task.FromResult<IReadOnlyList<LedgerEntry>>(list.ToList());
    }

    // ── IScoreCardSource (calibration / meta-analysis capture) ──

    /// <inheritdoc/>
    public Task SaveScoreCardsAsync(string campaignId, IReadOnlyList<ScoreCard> cards, CancellationToken ct = default)
    {
        // Concurrent campaigns can capture into the shared list at once; guard the plain List<T>.
        lock (_scoreCards) _scoreCards.AddRange(cards);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ScoreCard>> GetScoreCardsAsync(CancellationToken ct = default)
    {
        lock (_scoreCards) return Task.FromResult<IReadOnlyList<ScoreCard>>(_scoreCards.ToList());
    }

    /// <inheritdoc/>
    public Task SaveGroundTruthAsync(IReadOnlyDictionary<string, string> groundTruthByScenarioId, CancellationToken ct = default)
    {
        foreach ((string scenarioId, string truth) in groundTruthByScenarioId)
            _groundTruth[scenarioId] = truth;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, string>> GetGroundTruthAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_groundTruth));
}
