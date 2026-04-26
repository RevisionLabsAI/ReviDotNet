// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using MongoDB.Driver;

namespace ReviDotNet.Forge.Services.Mongo;

public interface IForgeMongoConnectionService
{
    IMongoDatabase Database { get; }
    IMongoCollection<T> GetCollection<T>(string collectionName);
}
