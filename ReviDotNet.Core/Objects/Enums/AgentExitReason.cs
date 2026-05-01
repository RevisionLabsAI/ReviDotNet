// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

public enum AgentExitReason
{
    Completed,
    GuardrailViolation,
    LoopDetected,
    Cancelled,
    Error,

    /// <summary>
    /// The LLM emitted signals not declared in the current state's loop transitions
    /// repeatedly enough to exhaust the per-activation correction budget. The runner
    /// nudges the LLM with a corrective message on the first miss; if it keeps failing
    /// the run terminates with this reason rather than spinning.
    /// </summary>
    InvalidSignal,

    /// <summary>
    /// A cost-budget guardrail (state-level or run-wide) was projected to be exceeded
    /// by the next LLM call. The runner exits gracefully with the partial output it
    /// has accumulated rather than burning through the budget cap.
    /// </summary>
    BudgetExceeded
}
