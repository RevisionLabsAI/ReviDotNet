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

        if (!string.IsNullOrWhiteSpace(q.AgentName))
        {
            var pattern = $@"(^|\s)agent:{System.Text.RegularExpressions.Regex.Escape(q.AgentName)}(\s|$)";
            f &= Builders<RlogEvent>.Filter.Regex(x => x.Tags, new BsonRegularExpression(pattern, "i"));
        }
        if (!string.IsNullOrWhiteSpace(q.AgentSessionId))
        {
            var pattern = $@"(^|\s)agent-session:{System.Text.RegularExpressions.Regex.Escape(q.AgentSessionId)}(\s|$)";
            f &= Builders<RlogEvent>.Filter.Regex(x => x.Tags, new BsonRegularExpression(pattern, "i"));
        }
        if (q.AgentMaxDepth is int maxDepth)
        {
            // Match agent-depth:N where N <= maxDepth (handles 0..9 cleanly; broader patterns require numeric extraction).
            var allowedDepths = string.Join('|', Enumerable.Range(0, Math.Max(0, maxDepth) + 1));
            var pattern = $@"(^|\s)agent-depth:({allowedDepths})(\s|$)";
            f &= Builders<RlogEvent>.Filter.Regex(x => x.Tags, new BsonRegularExpression(pattern, "i"));
        }

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

    public async Task<IReadOnlyList<AgentNameDto>> GetAgentNamesAsync(int take, CancellationToken ct = default)
    {
        if (take <= 0) return Array.Empty<AgentNameDto>();

        // Match any tags containing "agent:<name>", look at recent events to keep it bounded.
        var filter = Builders<RlogEvent>.Filter.Regex(x => x.Tags, new BsonRegularExpression("(^|\\s)agent:", "i"));
        var recent = await _col.Find(filter)
            .SortByDescending(x => x.Timestamp)
            .Limit(5000)
            .Project(x => new { x.Tags, x.Timestamp })
            .ToListAsync(ct);

        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var rx = new System.Text.RegularExpressions.Regex(@"(?:^|\s)agent:([^\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var r in recent)
        {
            if (string.IsNullOrEmpty(r.Tags)) continue;
            var m = rx.Match(r.Tags);
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            counts[name] = counts.GetValueOrDefault(name, 0) + 1;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(take)
            .Select(kv => new AgentNameDto(kv.Key, kv.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<AgentSessionDto>> GetAgentSessionsAsync(string agentName, int skip, int take, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentName) || take <= 0) return Array.Empty<AgentSessionDto>();

        // Pull recent events tagged with this agent and a start step. Aggregate by session id from tags.
        var startPattern = $@"(^|\s)agent:{System.Text.RegularExpressions.Regex.Escape(agentName)}(\s|$).*?(^|\s)agent-step:start(\s|$)";
        var filter = Builders<RlogEvent>.Filter.Regex(x => x.Tags, new BsonRegularExpression(startPattern, "is"));

        var docs = await _col.Find(filter)
            .SortByDescending(x => x.Timestamp)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(ct);

        var rx = new System.Text.RegularExpressions.Regex(@"(?:^|\s)agent-session:([^\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var list = new List<AgentSessionDto>(docs.Count);
        foreach (var d in docs)
        {
            if (string.IsNullOrEmpty(d.Tags)) continue;
            var m = rx.Match(d.Tags);
            if (!m.Success) continue;
            string sessionId = m.Groups[1].Value;

            // Count events for this session and find last seen.
            long count = 1;
            DateTime lastSeen = d.Timestamp;
            var sessionFilter = Builders<RlogEvent>.Filter.Regex(x => x.Tags,
                new BsonRegularExpression($@"(^|\s)agent-session:{System.Text.RegularExpressions.Regex.Escape(sessionId)}(\s|$)", "i"));
            count = await _col.CountDocumentsAsync(sessionFilter, cancellationToken: ct);
            var last = await _col.Find(sessionFilter).SortByDescending(x => x.Timestamp).Limit(1).FirstOrDefaultAsync(ct);
            if (last != null) lastSeen = last.Timestamp;

            list.Add(new AgentSessionDto(sessionId, agentName, d.Timestamp, lastSeen, count, d.Id));
        }
        return list;
    }

    public async Task<IReadOnlyList<RlogEvent>> GetSessionEventsAsync(string sessionId, int maxEvents = 5000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Array.Empty<RlogEvent>();

        var pattern = $@"(^|\s)agent-session:{System.Text.RegularExpressions.Regex.Escape(sessionId)}(\s|$)";
        var filter = Builders<RlogEvent>.Filter.Regex(x => x.Tags, new BsonRegularExpression(pattern, "i"));

        var direct = await _col.Find(filter)
            .SortBy(x => x.Timestamp)
            .Limit(maxEvents)
            .ToListAsync(ct);

        // Expand: also include events whose ParentId chains up to a session event but don't carry the session tag
        // (e.g. sub-agent root nests under tool-call but starts a new session). One BFS layer should be enough
        // for the common case; deeper nesting is bounded by MaxAgentDepth (default 3).
        var resultMap = direct.ToDictionary(e => e.Id ?? Guid.NewGuid().ToString(), e => e);
        var frontier = direct.Select(e => e.Id).Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToList();

        for (int hop = 0; hop < 5 && frontier.Count > 0 && resultMap.Count < maxEvents; hop++)
        {
            var children = await _col.Find(Builders<RlogEvent>.Filter.In(x => x.ParentId, frontier))
                .Limit(maxEvents - resultMap.Count)
                .ToListAsync(ct);
            if (children.Count == 0) break;
            var nextFrontier = new List<string>();
            foreach (var c in children)
            {
                if (string.IsNullOrEmpty(c.Id)) continue;
                if (resultMap.ContainsKey(c.Id)) continue;
                resultMap[c.Id] = c;
                nextFrontier.Add(c.Id);
            }
            frontier = nextFrontier;
        }

        return resultMap.Values.OrderBy(e => e.Timestamp).ToList();
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
