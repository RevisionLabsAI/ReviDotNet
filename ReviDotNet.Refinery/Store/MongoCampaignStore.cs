// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Revi.Refinery;

/// <summary>
/// Durable <see cref="ICampaignStore"/> backed by MongoDB. Campaigns and ledger entries survive process
/// restarts, giving the dashboard/CLI a real history and the <c>MetaAnalyzer</c> a corpus to mine.
/// <para>
/// <b>Serialization choice.</b> The SDK records (<see cref="Campaign"/>, <see cref="LedgerEntry"/> and the
/// nested <see cref="SuiteAggregate"/>/<see cref="VariantRecord"/>/<see cref="CampaignIteration"/>) are
/// immutable with <c>required</c> + <c>init</c> properties and <see cref="IReadOnlyDictionary{TKey,TValue}"/>
/// / <see cref="IReadOnlyList{T}"/> members. MongoDB's default POCO serializer struggles with required-init
/// records (it needs a writable surface or a hand-registered class map per type), so instead of registering
/// fragile <see cref="MongoDB.Bson.Serialization.BsonClassMap"/>s we serialize each record to JSON with
/// <see cref="System.Text.Json"/> (which fully understands records, required-init, and read-only collection
/// interfaces) and store that JSON inside a thin BSON envelope. A few scalar fields are lifted to the top of
/// the envelope so we can query/sort without rehydrating: campaigns are keyed by <c>_id</c> = Campaign.Id;
/// ledger entries carry top-level <c>campaignId</c> + <c>round</c> for the <see cref="GetLedgerAsync"/> query
/// (Find by CampaignId, ordered by Round). This keeps the store correct and immune to record-shape drift; the
/// price is that the JSON payload is opaque to ad-hoc Mongo queries, which we don't need here.
/// </para>
/// </summary>
public sealed class MongoCampaignStore : ICampaignStore, IScoreCardSource
{
    private const string CampaignsCollectionName = "refinery_campaigns";
    private const string LedgerCollectionName = "refinery_ledger";
    private const string ScoreCardsCollectionName = "refinery_scorecards";
    private const string GroundTruthCollectionName = "refinery_groundtruth";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly IMongoCollection<BsonDocument> _campaigns;
    private readonly IMongoCollection<BsonDocument> _ledger;
    private readonly IMongoCollection<BsonDocument> _scoreCards;
    private readonly IMongoCollection<BsonDocument> _groundTruth;

    /// <summary>
    /// Connects to <paramref name="databaseName"/> on <paramref name="connectionString"/> and ensures the
    /// ledger index (campaignId + round) exists for ordered reads.
    /// </summary>
    public MongoCampaignStore(string connectionString, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("connectionString is required", nameof(connectionString));
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("databaseName is required", nameof(databaseName));

        IMongoDatabase db = new MongoClient(connectionString).GetDatabase(databaseName);
        _campaigns = db.GetCollection<BsonDocument>(CampaignsCollectionName);
        _ledger = db.GetCollection<BsonDocument>(LedgerCollectionName);
        _scoreCards = db.GetCollection<BsonDocument>(ScoreCardsCollectionName);
        _groundTruth = db.GetCollection<BsonDocument>(GroundTruthCollectionName);

        // Index ledger by (campaignId, round) so GetLedgerAsync's filtered + sorted read is cheap. Best-effort:
        // a failure here (e.g. permissions) must not stop the store from functioning.
        try
        {
            IndexKeysDefinition<BsonDocument> keys = Builders<BsonDocument>.IndexKeys
                .Ascending("campaignId")
                .Ascending("round");
            _ledger.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(keys));
        }
        catch
        {
            // Ignore: the queries still work without the index, just less efficiently.
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(Campaign campaign, CancellationToken ct = default)
    {
        BsonDocument doc = new()
        {
            ["_id"] = campaign.Id,
            ["payload"] = ToPayload(campaign),
        };

        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", campaign.Id);
        await _campaigns.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    /// <inheritdoc/>
    public async Task<Campaign?> GetAsync(string id, CancellationToken ct = default)
    {
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        BsonDocument? doc = await _campaigns.Find(filter).FirstOrDefaultAsync(ct);
        return doc is null ? null : FromPayload<Campaign>(doc);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default)
    {
        List<BsonDocument> docs = await _campaigns.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        List<Campaign> result = [];
        foreach (BsonDocument doc in docs)
        {
            Campaign? c = FromPayload<Campaign>(doc);
            if (c is not null)
                result.Add(c);
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task AppendLedgerAsync(LedgerEntry entry, CancellationToken ct = default)
    {
        BsonDocument doc = new()
        {
            // Lift the fields GetLedgerAsync filters/sorts on; the full record lives in payload.
            ["campaignId"] = entry.CampaignId,
            ["round"] = entry.Round,
            ["payload"] = ToPayload(entry),
        };
        await _ledger.InsertOneAsync(doc, options: null, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default)
    {
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("campaignId", campaignId);
        SortDefinition<BsonDocument> sort = Builders<BsonDocument>.Sort.Ascending("round");
        List<BsonDocument> docs = await _ledger.Find(filter).Sort(sort).ToListAsync(ct);

        List<LedgerEntry> result = [];
        foreach (BsonDocument doc in docs)
        {
            LedgerEntry? e = FromPayload<LedgerEntry>(doc);
            if (e is not null)
                result.Add(e);
        }
        return result;
    }

    // ── IScoreCardSource (calibration / meta-analysis capture) ──

    /// <inheritdoc/>
    public async Task SaveScoreCardsAsync(string campaignId, IReadOnlyList<ScoreCard> cards, CancellationToken ct = default)
    {
        if (cards.Count == 0) return;

        // One document per card; lift campaignId + agentName so the corpus stays queryable without rehydrating.
        List<BsonDocument> docs = cards.Select(card => new BsonDocument
        {
            ["campaignId"] = campaignId,
            ["agentName"] = card.AgentName,
            ["payload"] = ToPayload(card),
        }).ToList();
        await _scoreCards.InsertManyAsync(docs, options: null, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScoreCard>> GetScoreCardsAsync(CancellationToken ct = default)
    {
        List<BsonDocument> docs = await _scoreCards.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        List<ScoreCard> result = [];
        foreach (BsonDocument doc in docs)
        {
            ScoreCard? c = FromPayload<ScoreCard>(doc);
            if (c is not null)
                result.Add(c);
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task SaveGroundTruthAsync(IReadOnlyDictionary<string, string> groundTruthByScenarioId, CancellationToken ct = default)
    {
        foreach ((string scenarioId, string truth) in groundTruthByScenarioId)
        {
            // Upsert by scenario id (_id) so re-running a campaign overwrites rather than duplicates.
            FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", scenarioId);
            BsonDocument doc = new() { ["_id"] = scenarioId, ["truth"] = truth };
            await _groundTruth.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true }, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetGroundTruthAsync(CancellationToken ct = default)
    {
        List<BsonDocument> docs = await _groundTruth.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        Dictionary<string, string> map = [];
        foreach (BsonDocument doc in docs)
        {
            if (doc.TryGetValue("_id", out BsonValue id) && id.IsString &&
                doc.TryGetValue("truth", out BsonValue truth) && truth.IsString)
                map[id.AsString] = truth.AsString;
        }
        return map;
    }

    /// <summary>Serializes a record to a JSON string stored as a BSON value (robust to required-init records).</summary>
    private static BsonValue ToPayload<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Rehydrates a record from the JSON payload stored under the <c>payload</c> field.</summary>
    private static T? FromPayload<T>(BsonDocument doc)
    {
        if (!doc.TryGetValue("payload", out BsonValue payload) || !payload.IsString)
            return default;
        return JsonSerializer.Deserialize<T>(payload.AsString, JsonOptions);
    }
}
