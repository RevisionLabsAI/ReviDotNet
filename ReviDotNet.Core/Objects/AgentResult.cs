// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// The result of a completed (or terminated) agent run.
/// </summary>
public class AgentResult
{
    /// <summary>
    /// The content field from the final step before the agent transitioned to [end].
    /// Null if the agent was terminated by a guardrail, loop detection, or error.
    /// </summary>
    public string? FinalOutput { get; set; }

    /// <summary>Describes why the agent run ended.</summary>
    public AgentExitReason ExitReason { get; set; }

    /// <summary>Ordered list of state names visited during the run (includes repeats for loops).</summary>
    public List<string> StateHistory { get; set; } = new();

    /// <summary>Total number of LLM calls made across the entire run.</summary>
    public int TotalSteps { get; set; }

    /// <summary>Describes which guardrail was violated, when ExitReason is GuardrailViolation.</summary>
    public string? GuardrailViolationMessage { get; set; }
}
