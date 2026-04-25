// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// The result returned by a tool after execution.
/// </summary>
public class ToolCallResult
{
    /// <summary>The name of the tool that was executed.</summary>
    public string ToolName { get; set; } = "";

    /// <summary>The text output from the tool. Appended to conversation history as a user message.</summary>
    public string? Output { get; set; }

    /// <summary>True if the tool call failed (e.g. tool not found, execution error).</summary>
    public bool Failed { get; set; }

    /// <summary>Error message when Failed is true.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Formats the result for injection into the conversation history.</summary>
    public string ToHistoryMessage()
    {
        if (Failed)
            return $"[Tool: {ToolName}] Error: {ErrorMessage}";
        return $"[Tool: {ToolName}] Result:\n{Output}";
    }
}
