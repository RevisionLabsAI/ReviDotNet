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

    /// <summary>The opening marker of a tool result delivered as data.</summary>
    /// <param name="mark">The run's marker value.</param>
    /// <returns>The marker line.</returns>
    public static string UntrustedOpen(string mark) => $"<<<untrusted-data {mark}>>>";

    /// <summary>The closing marker of a tool result delivered as data.</summary>
    /// <param name="mark">The run's marker value.</param>
    /// <returns>The marker line.</returns>
    public static string UntrustedClose(string mark) => $"<<<end-untrusted-data {mark}>>>";

    /// <summary>Formats the result for injection into the conversation history.</summary>
    /// <param name="untrustedMark">
    /// When supplied, a successful result's output is placed between markers carrying this value, so
    /// the model can be told that everything between them is data and not an instruction. The value
    /// is unknown to whatever produced the output, so the output cannot contain the closing marker.
    /// The first line is the same either way.
    /// </param>
    /// <returns>The message text.</returns>
    public string ToHistoryMessage(string? untrustedMark = null)
    {
        if (Failed)
            return $"[Tool: {ToolName}] Error: {ErrorMessage}";
        if (untrustedMark is null)
            return $"[Tool: {ToolName}] Result:\n{Output}";
        return $"[Tool: {ToolName}] Result:\n{UntrustedOpen(untrustedMark)}\n{Output}\n{UntrustedClose(untrustedMark)}";
    }
}
