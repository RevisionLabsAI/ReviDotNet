// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Represents a single transition edge in the agent loop graph.
/// </summary>
public class LoopTransition
{
    /// <summary>
    /// The target state name. Use "[end]" to terminate the agent, or "self" to re-enter the current state.
    /// </summary>
    public string TargetState { get; set; } = "";

    /// <summary>
    /// The signal that triggers this transition. Null means the transition is unconditional
    /// (fires when no signal-matched transition is found).
    /// </summary>
    public string? Signal { get; set; }
}
