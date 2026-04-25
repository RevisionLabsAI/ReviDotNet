// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Interface for built-in tools available to agent states without requiring a .tool config file.
/// </summary>
public interface IBuiltInTool
{
    /// <summary>Unique tool name used in the agent state's tools list and in LLM tool_calls.</summary>
    string Name { get; }

    /// <summary>Human-readable description of what the tool does.</summary>
    string Description { get; }

    /// <summary>
    /// Executes the tool with the given input and returns a result.
    /// </summary>
    Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token);
}
