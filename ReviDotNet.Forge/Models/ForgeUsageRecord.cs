// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ReviDotNet.Forge.Models;

public class ForgeUsageRecord
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientApiKeyPrefix { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string PromptName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public int FailoverAttempts { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public long LatencyMs { get; set; }
    public long TtftMs { get; set; }
    public bool WasStreaming { get; set; }
}
