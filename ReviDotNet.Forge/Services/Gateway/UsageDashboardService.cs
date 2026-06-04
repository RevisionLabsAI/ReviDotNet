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

    public async Task<List<ForgeUsageRecord>> GetRecentAsync(DateTime since, int limit = 500, UsageType? type = null)
    {
        if (_collection is not null)
        {
            var filter = Builders<ForgeUsageRecord>.Filter.Gte(r => r.Timestamp, since);
            if (type is not null)
                filter &= Builders<ForgeUsageRecord>.Filter.Eq(r => r.Type, type.Value);

            return await _collection
                .Find(filter)
                .SortByDescending(r => r.Timestamp)
                .Limit(limit)
                .ToListAsync();
        }

        lock (_ringBuffer)
        {
            return _ringBuffer
                .Where(r => r.Timestamp >= since && (type is null || r.Type == type.Value))
                .OrderByDescending(r => r.Timestamp)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>
    /// Request counts and success/failure split bucketed across the last hour,
    /// day and week. Pulls the widest window once and buckets in memory so the
    /// Inference/Embeddings dashboards are a single round-trip rather than three.
    /// Pass <paramref name="type"/> to scope to one operation, or null for all.
    /// </summary>
    public async Task<RequestPeriodSummary> GetPeriodSummaryAsync(UsageType? type = null)
    {
        var now = DateTime.UtcNow;
        var records = await GetRecentAsync(now.AddDays(-7), 10_000, type);

        PeriodStat Bucket(string label, DateTime since)
        {
            int total = 0, success = 0;
            foreach (var r in records)
            {
                if (r.Timestamp < since) continue;
                total++;
                if (r.Success) success++;
            }
            return new PeriodStat { Label = label, TotalRequests = total, SuccessCount = success };
        }

        return new RequestPeriodSummary
        {
            Periods =
            {
                Bucket("Last hour", now.AddHours(-1)),
                Bucket("Last 24 hours", now.AddDays(-1)),
                Bucket("Last 7 days", now.AddDays(-7)),
            }
        };
    }

    /// <summary>
    /// Per-client (ClientId) usage aggregates since the given instant. Powers
    /// the Clients page, where each client's traffic sits alongside its keys.
    /// Pass <paramref name="type"/> to scope to one operation, or null for all.
    /// </summary>
    public async Task<List<ClientUsage>> GetByClientAsync(DateTime since, UsageType? type = null)
    {
        var records = await GetRecentAsync(since, 10_000, type);
        return records
            .GroupBy(r => r.ClientId)
            .Select(g => new ClientUsage
            {
                ClientId = g.Key,
                TotalRequests = g.Count(),
                SuccessCount = g.Count(r => r.Success),
                TotalInputTokens = g.Sum(r => (long)r.InputTokens),
                TotalOutputTokens = g.Sum(r => (long)r.OutputTokens),
                LastRequestAt = g.Max(r => r.Timestamp),
            })
            .OrderByDescending(c => c.TotalRequests)
            .ToList();
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

/// <summary>Request totals for a set of trailing time windows.</summary>
public class RequestPeriodSummary
{
    public List<PeriodStat> Periods { get; set; } = new();
}

/// <summary>Request count and success/failure split for a single time window.</summary>
public class PeriodStat
{
    public string Label { get; set; } = string.Empty;
    public int TotalRequests { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount => TotalRequests - SuccessCount;
    public double SuccessRate => TotalRequests == 0 ? 0 : (double)SuccessCount / TotalRequests * 100;
}

/// <summary>Usage rolled up for a single client (ClientId).</summary>
public class ClientUsage
{
    public string ClientId { get; set; } = string.Empty;
    public int TotalRequests { get; set; }
    public int SuccessCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public DateTime LastRequestAt { get; set; }
    public double SuccessRate => TotalRequests == 0 ? 0 : (double)SuccessCount / TotalRequests * 100;
}
