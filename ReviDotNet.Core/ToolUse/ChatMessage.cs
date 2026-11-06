// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json.Linq;

namespace Revi.ToolUse;

/// <summary>
/// One message of a tool-using conversation. Content is kept as the provider's raw content blocks so
/// an assistant turn (text, thinking with its signature, tool calls, server-tool results) is replayed
/// to the provider byte-for-byte, which the provider requires for thinking blocks and tool calls.
/// Typed readers (<see cref="Text"/>, <see cref="ToolUses"/>) interpret the blocks for callers.
/// </summary>
public sealed class ChatMessage
{
    /// <summary>The role: <c>user</c>, <c>assistant</c>, or <c>system</c> (a mid-conversation operator note).</summary>
    public string Role { get; }

    /// <summary>The provider content blocks, in order.</summary>
    public IReadOnlyList<JObject> Blocks => _blocks;

    private readonly List<JObject> _blocks;

    /// <summary>Creates a message from raw blocks.</summary>
    public ChatMessage(string role, IEnumerable<JObject> blocks)
    {
        Role = role switch
        {
            "user" or "assistant" or "system" => role,
            _ => throw new ArgumentException($"Unknown role '{role}'.", nameof(role))
        };
        _blocks = blocks.ToList();
    }

    /// <summary>A user message with one text block.</summary>
    /// <param name="text">The text.</param>
    /// <param name="cacheBreakpoint">Whether to mark the block as a prompt-cache breakpoint.</param>
    public static ChatMessage User(string text, bool cacheBreakpoint = false)
        => new("user", new[] { TextBlock(text, cacheBreakpoint) });

    /// <summary>A user message with text and inline base64 images.</summary>
    public static ChatMessage User(string text, IEnumerable<(string MediaType, string Base64)> images)
    {
        var blocks = new List<JObject>();
        foreach (var (mediaType, base64) in images)
            blocks.Add(new JObject
            {
                ["type"] = "image",
                ["source"] = new JObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = base64 }
            });
        if (!string.IsNullOrEmpty(text)) blocks.Add(TextBlock(text, false));
        return new ChatMessage("user", blocks);
    }

    /// <summary>
    /// A mid-conversation system note. The provider requires it to follow a user message and to be
    /// either the last message or followed by an assistant message; <see cref="ToolChatSession"/>
    /// places it after the user message of a turn.
    /// </summary>
    public static ChatMessage SystemNote(string text) => new("system", new[] { TextBlock(text, false) });

    /// <summary>
    /// A user message carrying the results of every tool call of the preceding assistant message, in
    /// call order. One message for all of them: splitting them teaches the model not to call tools in
    /// parallel.
    /// </summary>
    public static ChatMessage ToolResults(IEnumerable<ToolResult> results)
    {
        var blocks = results.Select(r =>
        {
            var block = new JObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = r.ToolUseId,
                ["content"] = r.Content
            };
            if (r.IsError) block["is_error"] = true;
            return block;
        }).ToList();
        if (blocks.Count == 0) throw new ArgumentException("At least one tool result is needed.", nameof(results));
        return new ChatMessage("user", blocks);
    }

    /// <summary>An assistant message built from the provider's returned content array.</summary>
    public static ChatMessage Assistant(IEnumerable<JObject> blocks) => new("assistant", blocks);

    /// <summary>The concatenated text of the message's text blocks.</summary>
    public string Text => string.Concat(_blocks
        .Where(b => (string?)b["type"] == "text")
        .Select(b => (string?)b["text"] ?? string.Empty));

    /// <summary>The tool calls the message makes (assistant messages only).</summary>
    public IReadOnlyList<ToolUseRequest> ToolUses => _blocks
        .Where(b => (string?)b["type"] == "tool_use")
        .Select(b => new ToolUseRequest(
            (string?)b["id"] ?? string.Empty,
            (string?)b["name"] ?? string.Empty,
            b["input"] as JObject ?? new JObject()))
        .ToList();

    /// <summary>The tool results the message carries (user messages only).</summary>
    public IReadOnlyList<string> ToolResultIds => _blocks
        .Where(b => (string?)b["type"] == "tool_result")
        .Select(b => (string?)b["tool_use_id"] ?? string.Empty)
        .ToList();

    /// <summary>Whether the message contains any tool call.</summary>
    public bool HasToolUses => _blocks.Any(b => (string?)b["type"] == "tool_use");

    /// <summary>Adds a prompt-cache breakpoint to the last block.</summary>
    public void MarkCacheBreakpoint()
    {
        if (_blocks.Count == 0) return;
        _blocks[^1]["cache_control"] = new JObject { ["type"] = "ephemeral" };
    }

    /// <summary>Removes any cache breakpoint from the message's blocks.</summary>
    public void ClearCacheBreakpoint()
    {
        foreach (JObject block in _blocks) block.Remove("cache_control");
    }

    internal static JObject TextBlock(string text, bool cacheBreakpoint)
    {
        var block = new JObject { ["type"] = "text", ["text"] = text ?? string.Empty };
        if (cacheBreakpoint) block["cache_control"] = new JObject { ["type"] = "ephemeral" };
        return block;
    }

    /// <summary>Rebuilds a message from <see cref="ToJson"/> output.</summary>
    public static ChatMessage FromJson(JObject json)
    {
        string role = (string?)json["role"] ?? "user";
        IEnumerable<JObject> blocks = json["content"] switch
        {
            JArray array => array.OfType<JObject>().Select(b => (JObject)b.DeepClone()),
            JValue { Type: JTokenType.String } text => new[] { TextBlock((string?)text ?? string.Empty, false) },
            _ => Array.Empty<JObject>()
        };
        return new ChatMessage(role, blocks);
    }

    /// <summary>The wire form of the message.</summary>
    public JObject ToJson() => new()
    {
        ["role"] = Role,
        ["content"] = new JArray(_blocks.Select(b => b.DeepClone()))
    };
}

/// <summary>A tool call the model asked for.</summary>
/// <param name="Id">The provider's id for the call; the result must carry it back.</param>
/// <param name="Name">The tool name.</param>
/// <param name="Input">The parsed input object.</param>
public sealed record ToolUseRequest(string Id, string Name, JObject Input);

/// <summary>What a tool call returned, handed back to the model.</summary>
/// <param name="ToolUseId">The id of the call being answered.</param>
/// <param name="Content">The result text. Keep it compact; the model reads every byte.</param>
/// <param name="IsError">Whether the call failed. The model is told and may retry or explain.</param>
public sealed record ToolResult(string ToolUseId, string Content, bool IsError = false);
