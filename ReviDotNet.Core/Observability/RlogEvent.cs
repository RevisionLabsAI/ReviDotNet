// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Revi;

/// <summary>
/// Represents a log event that can be published to external consumers
/// </summary>
public class RlogEvent
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? Id { get; set; }

    [BsonElement("parentId")]
    [BsonIgnoreIfNull]
    public string? ParentId { get; set; }

    [BsonElement("timestamp")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [BsonElement("level")]
    public LogLevel Level { get; set; }

    [BsonElement("message")]
    public string Message { get; set; } = string.Empty;

    [BsonElement("identifier")]
    [BsonIgnoreIfNull]
    public string? Identifier { get; set; }

    [BsonElement("cycle")]
    public int Cycle { get; set; }

    [BsonElement("tags")]
    [BsonIgnoreIfNull]
    public string? Tags { get; set; }

    [BsonElement("object1")]
    [BsonIgnoreIfNull]
    public string? Object1 { get; set; }

    [BsonElement("object1Name")]
    [BsonIgnoreIfNull]
    public string? Object1Name { get; set; }

    [BsonElement("object2")]
    [BsonIgnoreIfNull]
    public string? Object2 { get; set; }

    [BsonElement("object2Name")]
    [BsonIgnoreIfNull]
    public string? Object2Name { get; set; }

    [BsonElement("file")]
    [BsonIgnoreIfNull]
    public string? File { get; set; }

    [BsonElement("member")]
    [BsonIgnoreIfNull]
    public string? Member { get; set; }

    [BsonElement("line")]
    [BsonIgnoreIfNull]
    public int? Line { get; set; }

    // Optional class/type name to pair with Member for UI display (ClassName.Method)
    [BsonElement("className")]
    [BsonIgnoreIfNull]
    public string? ClassName { get; set; }

    // Machine and instance identifiers for observability
    [BsonElement("machineId")]
    [BsonIgnoreIfNull]
    public string? MachineId { get; set; }

    [BsonElement("instanceId")]
    [BsonIgnoreIfNull]
    public string? InstanceId { get; set; }
}