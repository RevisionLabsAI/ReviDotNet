// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json.Linq;

namespace Revi.ToolUse;

/// <summary>How much the model thinks before answering.</summary>
public enum ThinkingMode
{
    /// <summary>Let the model decide (Anthropic <c>adaptive</c>). The default on current models.</summary>
    Adaptive,

    /// <summary>
    /// Think only between tool calls, never before the first text (Anthropic <c>between_tools</c>,
    /// Sonnet 5.5 and later at effort high or below). The lowest-latency setting for a chat turn.
    /// </summary>
    BetweenTools,

    /// <summary>Send no thinking parameter. The provider applies its own default.</summary>
    ProviderDefault
}

/// <summary>How the model may choose tools.</summary>
public enum ToolChoiceMode
{
    /// <summary>The model decides (<c>auto</c>).</summary>
    Auto,

    /// <summary>The model may not call any tool this request (<c>none</c>).</summary>
    None
}

/// <summary>One request to a tool-capable chat model.</summary>
public sealed class ToolChatRequest
{
    /// <summary>The model id, for example <c>claude-opus-5-5</c>.</summary>
    public required string Model { get; init; }

    /// <summary>The top-level system prompt. Keep it stable so the prefix caches.</summary>
    public string? System { get; init; }

    /// <summary>Whether to put a cache breakpoint after the system prompt.</summary>
    public bool CacheSystem { get; init; } = true;

    /// <summary>The conversation so far, oldest first.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>The tools the model may call. Order is part of the cached prefix: keep it stable.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = Array.Empty<ToolDefinition>();

    /// <summary>Whether to put a cache breakpoint after the last tool.</summary>
    public bool CacheTools { get; init; } = true;

    /// <summary>The thinking mode.</summary>
    public ThinkingMode Thinking { get; init; } = ThinkingMode.Adaptive;

    /// <summary>
    /// The effort level (<c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>), or null for
    /// the provider default. Lower effort means shorter answers and fewer tool calls.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// Whether thinking text is returned (<c>summarized</c>) or left empty (<c>omitted</c>, the
    /// provider default). Thinking happens and is billed either way.
    /// </summary>
    public bool ShowThinking { get; init; }

    /// <summary>The output token ceiling. Default 16 000.</summary>
    public int MaxTokens { get; init; } = 16_000;

    /// <summary>Stop sequences.</summary>
    public IReadOnlyList<string>? StopSequences { get; init; }

    /// <summary>Tool choice.</summary>
    public ToolChoiceMode ToolChoice { get; init; } = ToolChoiceMode.Auto;

    /// <summary>When true the model makes at most one tool call per message.</summary>
    public bool DisableParallelToolUse { get; init; }

    /// <summary>
    /// A JSON Schema the final text must satisfy (Anthropic structured outputs). Cannot be combined with
    /// citations. Null for free text.
    /// </summary>
    public JObject? OutputSchema { get; init; }

    /// <summary>Beta feature flags to send in the <c>anthropic-beta</c> header.</summary>
    public IReadOnlyList<string> Betas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Fallback routing on a safety refusal (Anthropic server-side fallbacks). When true the request
    /// carries <c>fallbacks: "default"</c> and the matching beta flag. Off by default; a refusal then
    /// surfaces as <see cref="ToolChatResponse.StopReason"/> <c>refusal</c>.
    /// </summary>
    public bool UseRefusalFallbacks { get; init; }

    /// <summary>Opaque metadata the provider stores with the request (<c>metadata.user_id</c>).</summary>
    public string? UserId { get; init; }

    /// <summary>Whether to stream. Set by the client for the streaming call.</summary>
    internal bool Stream { get; init; }
}

/// <summary>Token usage of one request.</summary>
/// <param name="InputTokens">Uncached input tokens.</param>
/// <param name="CacheReadTokens">Input tokens served from the prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to the prompt cache.</param>
/// <param name="OutputTokens">Output tokens, thinking included.</param>
public sealed record ChatUsage(long InputTokens, long CacheReadTokens, long CacheWriteTokens, long OutputTokens)
{
    /// <summary>All input tokens, cached or not.</summary>
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Zero usage.</summary>
    public static ChatUsage Zero { get; } = new(0, 0, 0, 0);

    /// <summary>Sums two usages.</summary>
    public static ChatUsage operator +(ChatUsage a, ChatUsage b)
        => new(a.InputTokens + b.InputTokens, a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens, a.OutputTokens + b.OutputTokens);
}

/// <summary>The model's complete answer to one request.</summary>
public sealed class ToolChatResponse
{
    /// <summary>The assistant message, with raw blocks for exact replay.</summary>
    public required ChatMessage Message { get; init; }

    /// <summary>
    /// Why the model stopped: <c>end_turn</c>, <c>tool_use</c>, <c>max_tokens</c>, <c>stop_sequence</c>,
    /// <c>pause_turn</c> (a server tool needs another request), or <c>refusal</c>.
    /// </summary>
    public required string StopReason { get; init; }

    /// <summary>The refusal category when <see cref="StopReason"/> is <c>refusal</c>.</summary>
    public string? RefusalCategory { get; init; }

    /// <summary>Token usage.</summary>
    public required ChatUsage Usage { get; init; }

    /// <summary>The model that answered.</summary>
    public string? Model { get; init; }

    /// <summary>The provider's message id.</summary>
    public string? Id { get; init; }

    /// <summary>The answer text.</summary>
    public string Text => Message.Text;

    /// <summary>The tool calls to execute before the next request.</summary>
    public IReadOnlyList<ToolUseRequest> ToolUses => Message.ToolUses;

    /// <summary>The thinking summary, when <see cref="ToolChatRequest.ShowThinking"/> was set.</summary>
    public string Thinking => string.Concat(Message.Blocks
        .Where(b => (string?)b["type"] == "thinking")
        .Select(b => (string?)b["thinking"] ?? string.Empty));

    /// <summary>Whether the model wants tool results before it can continue.</summary>
    public bool NeedsToolResults => StopReason == "tool_use" && ToolUses.Count > 0;
}
