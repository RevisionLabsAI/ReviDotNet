// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using MongoDB.Driver;
using ReviDotNet.Forge.Models;
using ReviDotNet.Forge.Services.Mongo;

namespace ReviDotNet.Forge.Services.Gateway;

public class UsageDashboardService
{
    private readonly IMongoCollection<ForgeUsageRecord>? _collection;
    private readonly LinkedList<ForgeUsageRecord> _ringBuffer = new();
    private const int RingBufferMax = 10_000;
    private const string Collection = "ForgeUsage";

    public UsageDashboardService(IForgeMongoConnectionService? mongo = null)
    {
        if (mongo is not null)
            _collection = mongo.GetCollection<ForgeUsageRecord>(Collection);
    }

    public async Task RecordAsync(ForgeUsageRecord record)
    {
        if (_collection is not null)
        {
            try { await _collection.InsertOneAsync(record); }
            catch { }
            return;
        }

        lock (_ringBuffer)
        {
            _ringBuffer.AddLast(record);
            if (_ringBuffer.Count > RingBufferMax)
                _ringBuffer.RemoveFirst();
        }
    }

    public async Task<List<ForgeUsageRecord>> GetRecentAsync(DateTime since, int limit = 500)
    {
        if (_collection is not null)
        {
            return await _collection
                .Find(r => r.Timestamp >= since)
                .SortByDescending(r => r.Timestamp)
                .Limit(limit)
                .ToListAsync();
        }

        lock (_ringBuffer)
        {
            return _ringBuffer
                .Where(r => r.Timestamp >= since)
                .OrderByDescending(r => r.Timestamp)
                .Take(limit)
                .ToList();
        }
    }

    public async Task<UsageSummary> GetSummaryAsync(DateTime since)
    {
        var records = await GetRecentAsync(since, 10_000);
        if (records.Count == 0) return new UsageSummary();

        var latencies = records.Where(r => r.Success).Select(r => r.LatencyMs).OrderBy(x => x).ToList();
        return new UsageSummary
        {
            TotalRequests = records.Count,
            SuccessCount = records.Count(r => r.Success),
            TotalInputTokens = records.Sum(r => r.InputTokens),
            TotalOutputTokens = records.Sum(r => r.OutputTokens),
            P50LatencyMs = latencies.Count > 0 ? latencies[latencies.Count / 2] : 0,
            P95LatencyMs = latencies.Count > 0 ? latencies[(int)(latencies.Count * 0.95)] : 0,
            ByProvider = records.GroupBy(r => r.ProviderName)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByModel = records.GroupBy(r => r.ModelName)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }
}

public class UsageSummary
{
    public int TotalRequests { get; set; }
    public int SuccessCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long P50LatencyMs { get; set; }
    public long P95LatencyMs { get; set; }
    public Dictionary<string, int> ByProvider { get; set; } = new();
    public Dictionary<string, int> ByModel { get; set; } = new();
    public double SuccessRate => TotalRequests == 0 ? 0 : (double)SuccessCount / TotalRequests * 100;
}
