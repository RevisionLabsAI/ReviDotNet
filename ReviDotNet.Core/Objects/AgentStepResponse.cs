// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;

namespace Revi;

/// <summary>
/// Structured JSON contract that every LLM step response must conform to.
/// The JSON schema is injected via GuidanceType.Json at call time to enforce this structure.
/// </summary>
public class AgentStepResponse
{
    /// <summary>
    /// The transition signal emitted by the LLM. Must match one of the [when: SIGNAL] clauses
    /// in the [[_loop]] definition for the current state. Null means no transition signal.
    /// </summary>
    [JsonProperty("signal")]
    public string? Signal { get; set; }

    /// <summary>
    /// Tool calls the LLM wants to execute. Only tools listed in the current state's
    /// tools configuration will be dispatched; others are silently ignored.
    /// </summary>
    [JsonProperty("tool_calls")]
    public List<AgentToolCall> ToolCalls { get; set; } = new();

    /// <summary>
    /// The main text output or reasoning for this step. Accumulated into the conversation history.
    /// This becomes the FinalOutput on the last step before [end].
    /// </summary>
    [JsonProperty("content")]
    public string Content { get; set; } = "";

    /// <summary>
    /// Optional extended reasoning/thinking text. Captured separately from Content so it
    /// can be surfaced as a discrete event in agent traces without polluting the main output.
    /// Not all models populate this; null/empty is the common case.
    /// </summary>
    [JsonProperty("thinking")]
    public string? Thinking { get; set; }
}

/// <summary>
/// A single tool call request from the LLM within an agent step.
/// </summary>
public class AgentToolCall
{
    /// <summary>The name of the tool to invoke (must match a built-in or registered custom tool).</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    /// <summary>Freeform input string passed to the tool's ExecuteAsync method.</summary>
    [JsonProperty("input")]
    public string Input { get; set; } = "";
}
