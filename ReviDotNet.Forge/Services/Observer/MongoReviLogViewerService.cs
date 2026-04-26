// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using MongoDB.Bson;
using MongoDB.Driver;
using Revi;
using ReviDotNet.Forge.Services.Mongo;

namespace ReviDotNet.Forge.Services.Observer;

public sealed class MongoReviLogViewerService : IReviLogViewerService
{
    private readonly IMongoCollection<RlogEvent> _col;
    private readonly IReviLogLimiter _limiter;

    public MongoReviLogViewerService(IForgeMongoConnectionService mongo, IReviLogLimiter limiter)
    {
        _col = mongo.GetCollection<RlogEvent>("LogEvents");
        EnsureIndexes(_col);
        _limiter = limiter;
    }

    public async Task<IReadOnlyList<ReviInstanceDto>> GetInstancesAsync(int skip, int take, CancellationToken ct = default)
    {
        if (take <= 0) return Array.Empty<ReviInstanceDto>();
        if (skip < 0) skip = 0;

        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                {"_id", "$instanceId"},
                {"first", new BsonDocument("$min", "$timestamp")},
                {"last",  new BsonDocument("$max", "$timestamp")},
                {"count", new BsonDocument("$sum", 1)}
            }),
            new BsonDocument("$sort", new BsonDocument("last", -1)),
            new BsonDocument("$skip", skip),
            new BsonDocument("$limit", take)
        };

        var docs = await _col.Database.GetCollection<BsonDocument>(_col.CollectionNamespace.CollectionName)
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        var list = new List<ReviInstanceDto>(docs.Count);
        foreach (var d in docs)
        {
            var id = d.GetValue("_id", BsonNull.Value).IsBsonNull ? string.Empty : d["_id"].AsString;
            var first = d.GetValue("first", BsonNull.Value).ToUniversalTime();
            var last = d.GetValue("last", BsonNull.Value).ToUniversalTime();
            var count = d.GetValue("count", 0).ToInt64();
            if (!string.IsNullOrWhiteSpace(id))
                list.Add(new ReviInstanceDto(id, first, last, count));
        }
        return list;
    }

    public async Task<long> CountInstancesAsync(CancellationToken ct = default)
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument { {"_id", "$instanceId"} }),
            new BsonDocument("$count", "count")
        };

        var docs = await _col.Database.GetCollection<BsonDocument>(_col.CollectionNamespace.CollectionName)
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        if (docs.Count == 0) return 0L;
        return docs[0].GetValue("count", 0).ToInt64();
    }

    public async Task<long> CountLogsAsync(ReviLogQuery q, CancellationToken ct = default)
    {
        var baseCount = await _col.CountDocumentsAsync(BuildFilter(q), cancellationToken: ct);
        if (_limiter.Entries.Count == 0) return baseCount;
        var sample = await GetLogsPageAsync(q with { Page = 0 }, ct);
        var suppressed = sample.Count(e => _limiter.IsSuppressed(e));
        if (sample.Count == 0) return baseCount;
        double ratio = (double)suppressed / sample.Count;
        return (long)Math.Max(0, Math.Round(baseCount * (1.0 - ratio)));
    }

    public async Task<IReadOnlyList<RlogEvent>> GetLogsPageAsync(ReviLogQuery q, CancellationToken ct = default)
    {
        if (q.PageSize <= 0) return Array.Empty<RlogEvent>();
        if (q.Page < 0) q = q with { Page = 0 };

        var filter = BuildFilter(q);
        var sort = q.Descending
            ? Builders<RlogEvent>.Sort.Descending(x => x.Timestamp)
            : Builders<RlogEvent>.Sort.Ascending(x => x.Timestamp);

        var cursor = await _col.Find(filter)
            .Sort(sort)
            .Skip(q.Page * q.PageSize)
            .Limit(q.PageSize)
            .ToListAsync(ct);

        if (_limiter.Entries.Count == 0) return cursor;
        return cursor.Where(e => !_limiter.IsSuppressed(e)).ToList();
    }

    public async Task<IReadOnlyList<RlogEvent>> GetChildrenAsync(IReadOnlyCollection<string> parentIds, CancellationToken ct = default)
    {
        if (parentIds == null || parentIds.Count == 0) return Array.Empty<RlogEvent>();
        var filter = Builders<RlogEvent>.Filter.In(x => x.ParentId, parentIds);
        return await _col.Find(filter).ToListAsync(ct);
    }

    public async Task<long> ClearInstanceLogsAsync(string instanceId, DateTime? olderThanUtc = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return 0L;

        FilterDefinition<RlogEvent> filter = Builders<RlogEvent>.Filter.Eq(x => x.InstanceId, instanceId);
        bool deleteAll = olderThanUtc is null;

        if (!deleteAll)
            filter &= Builders<RlogEvent>.Filter.Lt(x => x.Timestamp, olderThanUtc!.Value);

        DeleteResult result = await _col.DeleteManyAsync(filter, ct);

        if (deleteAll)
        {
            await _col.InsertOneAsync(new RlogEvent
            {
                InstanceId = instanceId,
                Timestamp = DateTime.UtcNow,
                Level = Revi.LogLevel.Info,
                Message = "Logs were cleared."
            }, cancellationToken: ct);
        }

        return result.DeletedCount;
    }

    private static FilterDefinition<RlogEvent> BuildFilter(ReviLogQuery q)
    {
        var f = Builders<RlogEvent>.Filter.Empty;
        if (!string.IsNullOrWhiteSpace(q.InstanceId))
            f &= Builders<RlogEvent>.Filter.Eq(x => x.InstanceId, q.InstanceId);
        if (q.From is not null)
            f &= Builders<RlogEvent>.Filter.Gte(x => x.Timestamp, q.From.Value);
        if (q.To is not null)
            f &= Builders<RlogEvent>.Filter.Lte(x => x.Timestamp, q.To.Value);
        if (q.Levels is { Count: > 0 })
            f &= Builders<RlogEvent>.Filter.In(x => x.Level, q.Levels);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var regex = new BsonRegularExpression(q.Search, "i");
            var msg = Builders<RlogEvent>.Filter.Regex(x => x.Message, regex);
            var tags = Builders<RlogEvent>.Filter.Regex(x => x.Tags, regex);
            var ident = Builders<RlogEvent>.Filter.Regex(x => x.Identifier, regex);
            var cls = Builders<RlogEvent>.Filter.Regex(x => x.ClassName, regex);
            var mem = Builders<RlogEvent>.Filter.Regex(x => x.Member, regex);

            var ors = msg | tags | ident | cls | mem;
            if (int.TryParse(q.Search, out var lineNum))
                ors |= Builders<RlogEvent>.Filter.Eq(x => x.Line, lineNum);

            f &= ors;
        }
        return f;
    }

    private static void EnsureIndexes(IMongoCollection<RlogEvent> col)
    {
        try
        {
            var keys = Builders<RlogEvent>.IndexKeys
                .Ascending(x => x.InstanceId)
                .Descending(x => x.Timestamp);
            col.Indexes.CreateOne(new CreateIndexModel<RlogEvent>(keys));
            col.Indexes.CreateOne(new CreateIndexModel<RlogEvent>(
                Builders<RlogEvent>.IndexKeys.Ascending(x => x.ParentId)));
        }
        catch { }
    }
}
