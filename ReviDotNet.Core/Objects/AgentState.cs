// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Configuration for a single named state within an agent loop.
/// Parsed from [[state.&lt;name&gt;]] sections in a .agent file.
/// </summary>
public class AgentState
{
    /// <summary>The unique name of this state (e.g. "search", "analyze").</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-readable description of what this state does.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional reference to a .pmt prompt file that defines the base system/instruction for this state.
    /// If set, the prompt's system and instruction are used as a starting point, then the state
    /// instruction (from [[_state.&lt;name&gt;.instruction]]) is appended.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Optional model name override for this state. If null, the runner selects a model via ModelManager.Find().
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Comma-separated list of tool names allowed in this state.
    /// Only tools in this list will be dispatched when the LLM requests them.
    /// </summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>
    /// The state-specific instruction text, from [[_state.&lt;name&gt;.instruction]] in the .agent file.
    /// Appended to the agent's global system prompt for every LLM call in this state.
    /// </summary>
    public string? Instruction { get; set; }

    /// <summary>Guardrail limits for this state. All limits are optional.</summary>
    public AgentGuardrails Guardrails { get; set; } = new();
}
