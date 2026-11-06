// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json.Linq;

namespace Revi.ToolUse;

/// <summary>
/// Builds Anthropic Messages API request bodies from a <see cref="ToolChatRequest"/> and reads the
/// non-streaming response. Owns the wire shape so the client and the session never touch JSON keys.
/// </summary>
public static class ClaudePayloads
{
    /// <summary>The beta flag for server-side refusal fallbacks in their routing-by-category form.</summary>
    public const string RefusalFallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>The API version header value every request carries.</summary>
    public const string ApiVersion = "2023-06-01";

    /// <summary>Builds the request body.</summary>
    public static JObject BuildBody(ToolChatRequest request)
    {
        var body = new JObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens
        };

        if (!string.IsNullOrEmpty(request.System))
        {
            var block = ChatMessage.TextBlock(request.System!, request.CacheSystem);
            body["system"] = new JArray(block);
        }

        var tools = new JArray();
        for (int i = 0; i < request.Tools.Count; i++)
        {
            ToolDefinition tool = request.Tools[i];
            JObject entry = ToolEntry(tool);
            bool breakpoint = tool.CacheBreakpoint || (request.CacheTools && i == request.Tools.Count - 1);
            if (breakpoint) entry["cache_control"] = new JObject { ["type"] = "ephemeral" };
            tools.Add(entry);
        }
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            if (request.ToolChoice == ToolChoiceMode.None)
                body["tool_choice"] = new JObject { ["type"] = "none" };
            else if (request.DisableParallelToolUse)
                body["tool_choice"] = new JObject { ["type"] = "auto", ["disable_parallel_tool_use"] = true };
        }

        body["messages"] = new JArray(request.Messages.Select(m => m.ToJson()));

        switch (request.Thinking)
        {
            case ThinkingMode.Adaptive:
            {
                var thinking = new JObject { ["type"] = "adaptive" };
                if (request.ShowThinking) thinking["display"] = "summarized";
                body["thinking"] = thinking;
                break;
            }
            case ThinkingMode.BetweenTools:
                // Takes no other field; display and effort changes are rejected alongside it.
                body["thinking"] = new JObject { ["type"] = "between_tools" };
                break;
            case ThinkingMode.ProviderDefault:
                break;
        }

        var outputConfig = new JObject();
        if (!string.IsNullOrEmpty(request.Effort)) outputConfig["effort"] = request.Effort!.ToLowerInvariant();
        if (request.OutputSchema is not null)
            outputConfig["format"] = new JObject { ["type"] = "json_schema", ["schema"] = StrictSchema(request.OutputSchema) };
        if (outputConfig.Count > 0) body["output_config"] = outputConfig;

        if (request.StopSequences is { Count: > 0 })
            body["stop_sequences"] = new JArray(request.StopSequences);

        if (request.UseRefusalFallbacks) body["fallbacks"] = "default";

        if (!string.IsNullOrEmpty(request.UserId))
            body["metadata"] = new JObject { ["user_id"] = request.UserId };

        if (request.Stream) body["stream"] = true;

        return body;
    }

    /// <summary>The beta flags a request needs, from its options plus what the caller listed.</summary>
    public static IReadOnlyList<string> Betas(ToolChatRequest request)
    {
        var betas = new List<string>(request.Betas);
        if (request.UseRefusalFallbacks && !betas.Contains(RefusalFallbackBeta)) betas.Add(RefusalFallbackBeta);
        return betas;
    }

    private static JObject ToolEntry(ToolDefinition tool)
    {
        if (tool.IsServerTool)
        {
            var server = new JObject { ["type"] = tool.ProviderType, ["name"] = tool.Name };
            if (tool.ProviderFields is not null)
                foreach (JProperty field in tool.ProviderFields.Properties()) server[field.Name] = field.Value.DeepClone();
            return server;
        }

        var entry = new JObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["input_schema"] = tool.Strict ? StrictSchema(tool.InputSchema!) : tool.InputSchema!.DeepClone()
        };
        if (tool.Strict) entry["strict"] = true;
        if (tool.DeferLoading) entry["defer_loading"] = true;
        return entry;
    }

    /// <summary>
    /// A copy of the schema with <c>additionalProperties: false</c> and a <c>required</c> list naming
    /// every property on every object, which strict tools and structured outputs demand.
    /// </summary>
    public static JObject StrictSchema(JObject schema)
    {
        var copy = (JObject)schema.DeepClone();
        MakeStrict(copy);
        return copy;
    }

    private static void MakeStrict(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                if ((string?)obj["type"] == "object" || obj["properties"] is JObject)
                {
                    obj["additionalProperties"] = false;
                    if (obj["properties"] is JObject props)
                    {
                        obj["required"] = new JArray(props.Properties().Select(p => p.Name));
                        foreach (JProperty p in props.Properties()) MakeStrict(p.Value);
                    }
                }
                if (obj["items"] is JToken items) MakeStrict(items);
                foreach (string key in new[] { "anyOf", "oneOf", "allOf" })
                    if (obj[key] is JArray variants) foreach (JToken v in variants) MakeStrict(v);
                break;
            case JArray arr:
                foreach (JToken item in arr) MakeStrict(item);
                break;
        }
    }

    /// <summary>Reads a complete (non-streaming) response body.</summary>
    public static ToolChatResponse ReadResponse(JObject body)
    {
        var blocks = (body["content"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
        return new ToolChatResponse
        {
            Message = ChatMessage.Assistant(blocks),
            StopReason = (string?)body["stop_reason"] ?? string.Empty,
            RefusalCategory = RefusalCategoryOf(body["stop_details"]),
            Usage = ReadUsage(body["usage"] as JObject),
            Model = (string?)body["model"],
            Id = (string?)body["id"]
        };
    }

    /// <summary>The refusal category of a <c>stop_details</c> value, which is <c>null</c> on every non-refusal.</summary>
    public static string? RefusalCategoryOf(JToken? stopDetails) => stopDetails is JObject details ? (string?)details["category"] : null;

    /// <summary>Reads a usage object; missing fields count as zero.</summary>
    public static ChatUsage ReadUsage(JObject? usage)
    {
        if (usage is null) return ChatUsage.Zero;
        return new ChatUsage(
            (long?)usage["input_tokens"] ?? 0,
            (long?)usage["cache_read_input_tokens"] ?? 0,
            (long?)usage["cache_creation_input_tokens"] ?? 0,
            (long?)usage["output_tokens"] ?? 0);
    }
}
