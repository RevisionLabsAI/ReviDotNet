// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Guardrail configuration for a single agent state.
/// All limits are nullable — an unset field means no limit is enforced.
/// Maps to [[state.&lt;name&gt;.guardrails]] in the .agent file.
/// </summary>
public class AgentGuardrails
{
    /// <summary>Maximum number of times this state may be activated across the entire agent run.</summary>
    [RConfigProperty("cycle-limit")]
    public int? CycleLimit { get; set; }

    /// <summary>Maximum number of LLM calls within a single activation of this state.</summary>
    [RConfigProperty("max-steps")]
    public int? MaxSteps { get; set; }

    /// <summary>Timeout in seconds for a single activation of this state.</summary>
    [RConfigProperty("timeout")]
    public int? TimeoutSeconds { get; set; }

    /// <summary>Maximum USD cost budget for a single activation of this state.</summary>
    [RConfigProperty("cost-budget")]
    public decimal? CostBudget { get; set; }

    /// <summary>Maximum number of tool calls allowed in a single activation of this state.</summary>
    [RConfigProperty("tool-call-limit")]
    public int? ToolCallLimit { get; set; }

    /// <summary>Maximum number of retries on a failed LLM call before giving up.</summary>
    [RConfigProperty("retry-limit")]
    public int? RetryLimit { get; set; }

    /// <summary>When true, the runner detects repeating state sequences and exits with LoopDetected.</summary>
    [RConfigProperty("loop-detection")]
    public bool? LoopDetection { get; set; }

    /// <summary>
    /// Maximum sub-agent nesting depth permitted from this state. If the LLM invokes
    /// invoke_agent and the resulting depth would exceed this, the call is refused.
    /// Null means inherit the runner-wide default (3).
    /// </summary>
    [RConfigProperty("max-agent-depth")]
    public int? MaxAgentDepth { get; set; }
}
