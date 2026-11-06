// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using Newtonsoft.Json.Linq;

namespace Revi.ToolUse;

/// <summary>
/// Turns the Anthropic Messages streaming events into <see cref="ToolChatEvent"/>s while rebuilding
/// the assistant message's raw content blocks, so the finished message can be replayed exactly.
/// Unknown block types are kept as the provider sent them; unknown delta types are ignored.
/// </summary>
public sealed class ClaudeStreamParser
{
    private readonly List<JObject> _blocks = new();
    private readonly Dictionary<int, StringBuilder> _partialJson = new();
    private string? _id;
    private string? _model;
    private string _stopReason = string.Empty;
    private string? _refusalCategory;
    private ChatUsage _usage = ChatUsage.Zero;
    private long _startInput, _startCacheRead, _startCacheWrite;

    /// <summary>The response once <c>message_stop</c> arrived; null before.</summary>
    public ToolChatResponse? Completed { get; private set; }

    /// <summary>The text of a provider <c>error</c> event, if one arrived.</summary>
    public string? Error { get; private set; }

    /// <summary>Feeds one SSE event and returns the events it produced.</summary>
    /// <param name="eventType">The SSE <c>event:</c> name, or null.</param>
    /// <param name="data">The SSE <c>data:</c> JSON.</param>
    public IReadOnlyList<ToolChatEvent> Feed(string? eventType, string data)
    {
        var produced = new List<ToolChatEvent>();
        if (string.IsNullOrWhiteSpace(data)) return produced;

        JObject json;
        try { json = JObject.Parse(data); }
        catch (Newtonsoft.Json.JsonException) { return produced; }

        string type = (string?)json["type"] ?? eventType ?? string.Empty;
        switch (type)
        {
            case "message_start":
            {
                var message = json["message"] as JObject;
                _id = (string?)message?["id"];
                _model = (string?)message?["model"];
                ChatUsage start = ClaudePayloads.ReadUsage(message?["usage"] as JObject);
                _startInput = start.InputTokens;
                _startCacheRead = start.CacheReadTokens;
                _startCacheWrite = start.CacheWriteTokens;
                break;
            }
            case "content_block_start":
            {
                int index = (int?)json["index"] ?? _blocks.Count;
                var block = json["content_block"] as JObject ?? new JObject();
                var copy = (JObject)block.DeepClone();
                while (_blocks.Count <= index) _blocks.Add(new JObject());
                _blocks[index] = copy;
                string blockType = (string?)copy["type"] ?? string.Empty;
                if (blockType == "tool_use")
                {
                    _partialJson[index] = new StringBuilder();
                    produced.Add(new ToolUseStartedEvent((string?)copy["id"] ?? string.Empty, (string?)copy["name"] ?? string.Empty));
                }
                else if (blockType == "server_tool_use")
                {
                    _partialJson[index] = new StringBuilder();
                }
                else if (blockType == "text" && (string?)copy["text"] is { Length: > 0 } initial)
                {
                    produced.Add(new TextDeltaEvent(initial));
                }
                break;
            }
            case "content_block_delta":
            {
                int index = (int?)json["index"] ?? -1;
                if (index < 0 || index >= _blocks.Count) break;
                JObject block = _blocks[index];
                var delta = json["delta"] as JObject;
                switch ((string?)delta?["type"])
                {
                    case "text_delta":
                    {
                        string text = (string?)delta!["text"] ?? string.Empty;
                        block["text"] = ((string?)block["text"] ?? string.Empty) + text;
                        if (text.Length > 0) produced.Add(new TextDeltaEvent(text));
                        break;
                    }
                    case "thinking_delta":
                    {
                        string text = (string?)delta!["thinking"] ?? string.Empty;
                        block["thinking"] = ((string?)block["thinking"] ?? string.Empty) + text;
                        if (text.Length > 0) produced.Add(new ThinkingDeltaEvent(text));
                        break;
                    }
                    case "signature_delta":
                        block["signature"] = ((string?)block["signature"] ?? string.Empty) + ((string?)delta!["signature"] ?? string.Empty);
                        break;
                    case "input_json_delta":
                    {
                        string partial = (string?)delta!["partial_json"] ?? string.Empty;
                        if (!_partialJson.TryGetValue(index, out StringBuilder? sb)) _partialJson[index] = sb = new StringBuilder();
                        sb.Append(partial);
                        if ((string?)block["type"] == "tool_use" && partial.Length > 0)
                            produced.Add(new ToolUseInputDeltaEvent((string?)block["id"] ?? string.Empty, partial));
                        break;
                    }
                    case "citations_delta":
                    {
                        if (block["citations"] is not JArray citations) block["citations"] = citations = new JArray();
                        if (delta!["citation"] is JToken citation) citations.Add(citation.DeepClone());
                        break;
                    }
                }
                break;
            }
            case "content_block_stop":
            {
                int index = (int?)json["index"] ?? -1;
                if (index < 0 || index >= _blocks.Count) break;
                JObject block = _blocks[index];
                if (_partialJson.Remove(index, out StringBuilder? sb))
                {
                    string raw = sb.ToString();
                    JObject input;
                    if (string.IsNullOrWhiteSpace(raw)) input = new JObject();
                    else
                    {
                        try { input = JObject.Parse(raw); }
                        catch (Newtonsoft.Json.JsonException)
                        {
                            // Keep what arrived so the caller can show it, and mark it for the loop to refuse.
                            input = new JObject { ["__invalid_json"] = raw };
                        }
                    }
                    block["input"] = input;
                    if ((string?)block["type"] == "tool_use")
                        produced.Add(new ToolUseCompletedEvent(new ToolUseRequest((string?)block["id"] ?? string.Empty, (string?)block["name"] ?? string.Empty, input)));
                }
                break;
            }
            case "message_delta":
            {
                if (json["delta"] is JObject delta)
                {
                    if ((string?)delta["stop_reason"] is { } reason) _stopReason = reason;
                    _refusalCategory = (string?)delta["stop_details"]?["category"] ?? _refusalCategory;
                }
                if (json["usage"] is JObject usage)
                {
                    // message_delta carries cumulative output tokens; input tokens came with message_start,
                    // but a later delta may restate them, so prefer the delta's values when present.
                    _usage = new ChatUsage(
                        (long?)usage["input_tokens"] ?? _startInput,
                        (long?)usage["cache_read_input_tokens"] ?? _startCacheRead,
                        (long?)usage["cache_creation_input_tokens"] ?? _startCacheWrite,
                        (long?)usage["output_tokens"] ?? _usage.OutputTokens);
                }
                break;
            }
            case "message_stop":
            {
                if (_usage == ChatUsage.Zero) _usage = new ChatUsage(_startInput, _startCacheRead, _startCacheWrite, 0);
                Completed = new ToolChatResponse
                {
                    Message = ChatMessage.Assistant(_blocks.Where(b => b.Count > 0)),
                    StopReason = _stopReason,
                    RefusalCategory = _refusalCategory,
                    Usage = _usage,
                    Model = _model,
                    Id = _id
                };
                produced.Add(new MessageCompletedEvent(Completed));
                break;
            }
            case "error":
            {
                Error = (string?)json["error"]?["message"] ?? data;
                break;
            }
            // "ping" and anything unknown: nothing to do.
        }

        return produced;
    }
}
