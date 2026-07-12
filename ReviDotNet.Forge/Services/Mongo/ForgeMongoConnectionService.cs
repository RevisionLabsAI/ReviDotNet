// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace ReviDotNet.Forge.Services.Mongo;

public sealed class ForgeMongoConnectionService : IForgeMongoConnectionService
{
    private readonly IMongoDatabase _database;

    public ForgeMongoConnectionService(IConfiguration configuration)
    {
        string connectionString = configuration["Observer:MongoDb:ConnectionString"]!;
        string databaseName = configuration["Observer:MongoDb:DatabaseName"] ?? "ReviForge";

        // Fail fast when Mongo is down: the default 30s server-selection timeout turns every Observer
        // query and log-sink insert into a half-minute hang; 3s surfaces the outage without the stall.
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        var client = new MongoClient(settings);
        _database = client.GetDatabase(databaseName);
    }

    public IMongoDatabase Database => _database;

    public IMongoCollection<T> GetCollection<T>(string collectionName) =>
        _database.GetCollection<T>(collectionName);
}
