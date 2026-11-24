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

    /// <summary>
    /// Why the run did not complete, in words: which guardrail was violated, which deadline passed, which
    /// tool failed on the last step, or why a provider response was refused. Null for a completed run.
    /// </summary>
    public string? GuardrailViolationMessage { get; set; }

    /// <summary>
    /// The signal of the step that ended the run (upper-cased), or null when the run ended without one —
    /// on a guardrail, an error, or an unconditional transition. Lets a caller tell, say, <c>DONE</c> from
    /// <c>NO_CHANGE</c> when both lead to <c>[end]</c>.
    /// </summary>
    public string? FinalSignal { get; set; }

    /// <summary>How many tool calls failed across the whole run, including calls refused before running.</summary>
    public int FailedToolCalls { get; set; }

    /// <summary>
    /// The run's session id (matches the <c>agent-session:</c> tag on every emitted log event), so a
    /// caller can correlate this result with its captured trace. Empty if not set.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Total provider-reported USD cost across the whole run (sum of every LLM call's input/output tokens
    /// × the model's per-million rates). 0 when no cost rates are configured.
    /// </summary>
    public decimal Cost { get; set; }
}
