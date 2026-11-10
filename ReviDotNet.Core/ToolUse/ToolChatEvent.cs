// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.ToolUse;

/// <summary>An event of a streamed model answer or of a session turn.</summary>
public abstract record ToolChatEvent;

/// <summary>A piece of answer text arrived.</summary>
/// <param name="Text">The delta.</param>
public sealed record TextDeltaEvent(string Text) : ToolChatEvent;

/// <summary>A piece of thinking summary arrived (only with <see cref="ToolChatRequest.ShowThinking"/>).</summary>
/// <param name="Text">The delta.</param>
public sealed record ThinkingDeltaEvent(string Text) : ToolChatEvent;

/// <summary>The model began a tool call. Its input streams in afterwards.</summary>
/// <param name="Id">The call id.</param>
/// <param name="Name">The tool name.</param>
public sealed record ToolUseStartedEvent(string Id, string Name) : ToolChatEvent;

/// <summary>A piece of a tool call's input JSON arrived.</summary>
/// <param name="Id">The call id.</param>
/// <param name="PartialJson">The delta; concatenate to get the input.</param>
public sealed record ToolUseInputDeltaEvent(string Id, string PartialJson) : ToolChatEvent;

/// <summary>A tool call's input is complete.</summary>
/// <param name="Request">The call.</param>
public sealed record ToolUseCompletedEvent(ToolUseRequest Request) : ToolChatEvent;

/// <summary>The model's message is complete.</summary>
/// <param name="Response">The full response.</param>
public sealed record MessageCompletedEvent(ToolChatResponse Response) : ToolChatEvent;

/// <summary>A session began executing a tool call (session turns only).</summary>
/// <param name="Request">The call.</param>
public sealed record ToolCallStartedEvent(ToolUseRequest Request) : ToolChatEvent;

/// <summary>A session finished executing a tool call (session turns only).</summary>
/// <param name="Request">The call.</param>
/// <param name="Result">What went back to the model.</param>
/// <param name="Elapsed">How long the tool took.</param>
public sealed record ToolCallFinishedEvent(ToolUseRequest Request, ToolResult Result, TimeSpan Elapsed) : ToolChatEvent;

/// <summary>
/// The session paused because one or more tool calls need the user's confirmation (session turns only).
/// The turn ends here; call <see cref="ToolChatSession.ResolvePendingAsync"/> to continue.
/// </summary>
/// <param name="Pending">The calls awaiting a decision, with the gate's explanation of each.</param>
public sealed record ConfirmationRequiredEvent(IReadOnlyList<PendingToolCall> Pending) : ToolChatEvent;

/// <summary>The session turn ended (session turns only).</summary>
/// <param name="StopReason">The final stop reason.</param>
/// <param name="Usage">The usage of every request in the turn.</param>
/// <param name="Text">The final answer text (the last assistant message's text).</param>
public sealed record TurnCompletedEvent(string StopReason, ChatUsage Usage, string Text) : ToolChatEvent;

/// <summary>A tool call waiting on the user.</summary>
/// <param name="Request">The call.</param>
/// <param name="Summary">What the gate says the call would do, for the confirmation prompt.</param>
public sealed record PendingToolCall(ToolUseRequest Request, string Summary);
