// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// Represents a Responses API item, which can be used as input or output context.
/// </summary>
public class Item
{
    /// <summary>
    /// Gets or sets the item type (for example: "message", "function_call", "function_call_output").
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the role for message-type items (for example: "system", "user", "assistant").
    /// </summary>
    [JsonProperty("role")]
    public string? Role { get; set; }

    /// <summary>
    /// Gets or sets the content payload for the item. This may be a string or a structured object.
    /// </summary>
    [JsonProperty("content")]
    public object? Content { get; set; }

    /// <summary>
    /// Gets or sets the item name, when applicable (for example, tool or function name).
    /// </summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the call identifier for tool/function items.
    /// </summary>
    [JsonProperty("call_id")]
    public string? CallId { get; set; }

    /// <summary>
    /// Gets or sets the arguments payload for function/tool calls.
    /// </summary>
    [JsonProperty("arguments")]
    public object? Arguments { get; set; }

    /// <summary>
    /// Gets or sets the output payload for function/tool results.
    /// </summary>
    [JsonProperty("output")]
    public object? Output { get; set; }

    /// <summary>
    /// Gets or sets additional properties for items that include custom fields.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JToken> AdditionalProperties { get; set; } = [];
}