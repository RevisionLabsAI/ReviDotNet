// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;

namespace Revi.Refinery;

/// <summary>
/// Durable <see cref="ICampaignStore"/> backed by plain JSON files — campaigns, ledger entries, score
/// cards, and ground truth survive process restarts with no external database. Intended as the default
/// durable store for single-host Forge installs; <see cref="MongoCampaignStore"/> remains the choice when
/// a shared/queryable history is needed.
/// <para>
/// Layout under the root directory: <c>campaigns/{id}.json</c> (one file per campaign, atomically
/// replaced on save), <c>ledger/{campaignId}.jsonl</c> and <c>scorecards/{campaignId}.jsonl</c>
/// (append-only JSON Lines), and <c>groundtruth.json</c> (a single map, rewritten on save). The same
/// System.Text.Json serialization the Mongo store uses handles the required-init SDK records.
/// </para>
/// <para>
/// Concurrency: a single process-wide semaphore serializes all file access. Campaign saves are frequent
/// (once per completed run, for the live spend meters) but tiny, so contention is negligible; the
/// semaphore's job is preventing interleaved appends and read-during-replace races. Multi-process use is
/// NOT supported (run one Forge per store directory).
/// </para>
/// </summary>
public sealed class FileCampaignStore : ICampaignStore, IScoreCardSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _campaignsDir;
    private readonly string _ledgerDir;
    private readonly string _scoreCardsDir;
    private readonly string _groundTruthPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store rooted at <paramref name="rootDirectory"/> (created if missing).</summary>
    public FileCampaignStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("rootDirectory is required", nameof(rootDirectory));

        _campaignsDir = Path.Combine(rootDirectory, "campaigns");
        _ledgerDir = Path.Combine(rootDirectory, "ledger");
        _scoreCardsDir = Path.Combine(rootDirectory, "scorecards");
        _groundTruthPath = Path.Combine(rootDirectory, "groundtruth.json");

        Directory.CreateDirectory(_campaignsDir);
        Directory.CreateDirectory(_ledgerDir);
        Directory.CreateDirectory(_scoreCardsDir);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(Campaign campaign, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(campaign, JsonOptions);
        string path = CampaignPath(campaign.Id);
        string tmp = path + ".tmp";

        await _gate.WaitAsync(ct);
        try
        {
            // Write-then-replace so a crash mid-write can never truncate the previous good snapshot.
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<Campaign?> GetAsync(string id, CancellationToken ct = default)
    {
        string path = CampaignPath(id);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return null;
            return Deserialize<Campaign>(await File.ReadAllTextAsync(path, ct));
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            List<Campaign> result = [];
            foreach (string file in Directory.EnumerateFiles(_campaignsDir, "*.json"))
            {
                if (Deserialize<Campaign>(await File.ReadAllTextAsync(file, ct)) is { } campaign)
                    result.Add(campaign);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task AppendLedgerAsync(LedgerEntry entry, CancellationToken ct = default)
    {
        string line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await _gate.WaitAsync(ct);
        try { await File.AppendAllTextAsync(JsonlPath(_ledgerDir, entry.CampaignId), line, ct); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default)
    {
        string path = JsonlPath(_ledgerDir, campaignId);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return [];
            List<LedgerEntry> result = [];
            foreach (string line in await File.ReadAllLinesAsync(path, ct))
            {
                if (Deserialize<LedgerEntry>(line) is { } entry)
                    result.Add(entry);
            }
            return [.. result.OrderBy(e => e.Round)];
        }
        finally { _gate.Release(); }
    }

    // ── IScoreCardSource (calibration / meta-analysis capture) ──

    /// <inheritdoc/>
    public async Task SaveScoreCardsAsync(string campaignId, IReadOnlyList<ScoreCard> cards, CancellationToken ct = default)
    {
        if (cards.Count == 0) return;
        string lines = string.Concat(cards.Select(c => JsonSerializer.Serialize(c, JsonOptions) + Environment.NewLine));
        await _gate.WaitAsync(ct);
        try { await File.AppendAllTextAsync(JsonlPath(_scoreCardsDir, campaignId), lines, ct); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScoreCard>> GetScoreCardsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            List<ScoreCard> result = [];
            foreach (string file in Directory.EnumerateFiles(_scoreCardsDir, "*.jsonl"))
            {
                foreach (string line in await File.ReadAllLinesAsync(file, ct))
                {
                    if (Deserialize<ScoreCard>(line) is { } card)
                        result.Add(card);
                }
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task SaveGroundTruthAsync(IReadOnlyDictionary<string, string> groundTruthByScenarioId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Dictionary<string, string> map = File.Exists(_groundTruthPath)
                ? Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_groundTruthPath, ct)) ?? []
                : [];
            foreach ((string scenarioId, string truth) in groundTruthByScenarioId)
                map[scenarioId] = truth;

            string tmp = _groundTruthPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(map, JsonOptions), ct);
            File.Move(tmp, _groundTruthPath, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetGroundTruthAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_groundTruthPath)) return new Dictionary<string, string>();
            return Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_groundTruthPath, ct))
                   ?? new Dictionary<string, string>();
        }
        finally { _gate.Release(); }
    }

    private string CampaignPath(string id) => Path.Combine(_campaignsDir, Sanitize(id) + ".json");

    private static string JsonlPath(string dir, string campaignId) =>
        Path.Combine(dir, Sanitize(campaignId) + ".jsonl");

    /// <summary>Keeps ids filesystem-safe (campaign ids are GUID-ish today; this guards future drift).</summary>
    private static string Sanitize(string id) =>
        string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <summary>Tolerant deserialize: a corrupt line/file is skipped rather than sinking the whole store.</summary>
    private static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
