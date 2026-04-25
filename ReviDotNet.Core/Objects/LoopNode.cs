// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Represents a state node in the agent loop graph, holding all outbound transitions.
/// </summary>
public class LoopNode
{
    /// <summary>The name of the state this node represents.</summary>
    public string StateName { get; set; } = "";

    /// <summary>Ordered list of outbound transitions. Evaluated top-to-bottom on each step.</summary>
    public List<LoopTransition> Transitions { get; set; } = new();
}
