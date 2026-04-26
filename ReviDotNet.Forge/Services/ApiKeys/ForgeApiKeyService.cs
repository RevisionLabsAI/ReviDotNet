// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Driver;
using ReviDotNet.Forge.Models;
using ReviDotNet.Forge.Services.Mongo;

namespace ReviDotNet.Forge.Services.ApiKeys;

public class ForgeApiKeyService : IForgeApiKeyService
{
    private const string Collection = "ForgeApiKeys";
    private const int CacheSeconds = 60;

    private readonly IMongoCollection<ForgeApiKey>? _collection;
    private readonly IMemoryCache _cache;
    private readonly List<ForgeApiKey> _inMemoryKeys = [];

    public ForgeApiKeyService(IMemoryCache cache, IForgeMongoConnectionService? mongo = null)
    {
        _cache = cache;
        if (mongo is not null)
            _collection = mongo.GetCollection<ForgeApiKey>(Collection);
    }

    public async Task<List<ForgeApiKey>> GetAllAsync()
    {
        if (_collection is null) return [.. _inMemoryKeys];
        return await _collection.Find(_ => true).ToListAsync();
    }

    public async Task<(ForgeApiKey Key, string RawKey)> CreateAsync(string clientId)
    {
        var raw = GenerateRawKey();
        var key = new ForgeApiKey
        {
            ClientId = clientId,
            KeyHash = HashKey(raw),
            KeyPrefix = raw.Length >= 8 ? raw[..8] : raw,
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        if (_collection is not null)
            await _collection.InsertOneAsync(key);
        else
            _inMemoryKeys.Add(key);

        _cache.Remove("forge_api_keys");
        return (key, raw);
    }

    public async Task<bool> SetEnabledAsync(string id, bool enabled)
    {
        if (_collection is null)
        {
            var k = _inMemoryKeys.FirstOrDefault(x => x.Id == id);
            if (k is null) return false;
            k.Enabled = enabled;
            _cache.Remove("forge_api_keys");
            return true;
        }

        var update = Builders<ForgeApiKey>.Update.Set(x => x.Enabled, enabled);
        var result = await _collection.UpdateOneAsync(x => x.Id == id, update);
        _cache.Remove("forge_api_keys");
        return result.ModifiedCount > 0;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (_collection is null)
        {
            var removed = _inMemoryKeys.RemoveAll(x => x.Id == id) > 0;
            _cache.Remove("forge_api_keys");
            return removed;
        }

        var result = await _collection.DeleteOneAsync(x => x.Id == id);
        _cache.Remove("forge_api_keys");
        return result.DeletedCount > 0;
    }

    public async Task<string?> ValidateAsync(string rawKey)
    {
        var hash = HashKey(rawKey);
        var keys = await _cache.GetOrCreateAsync("forge_api_keys", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheSeconds);
            return await GetAllAsync();
        }) ?? [];

        var match = keys.FirstOrDefault(k => k.Enabled && k.KeyHash == hash);
        if (match is null) return null;

        _ = UpdateLastUsedAsync(match.Id!);
        return match.ClientId;
    }

    private async Task UpdateLastUsedAsync(string id)
    {
        try
        {
            if (_collection is null) return;
            var update = Builders<ForgeApiKey>.Update.Set(x => x.LastUsedAt, DateTime.UtcNow);
            await _collection.UpdateOneAsync(x => x.Id == id, update);
        }
        catch { }
    }

    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "forge_" + Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    private static string HashKey(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
