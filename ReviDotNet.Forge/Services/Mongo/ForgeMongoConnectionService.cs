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
        string databaseName = configuration["Observer:MongoDb:DatabaseName"] ?? "BetterNamer";

        var client = new MongoClient(connectionString);
        _database = client.GetDatabase(databaseName);
    }

    public IMongoDatabase Database => _database;

    public IMongoCollection<T> GetCollection<T>(string collectionName) =>
        _database.GetCollection<T>(collectionName);
}
