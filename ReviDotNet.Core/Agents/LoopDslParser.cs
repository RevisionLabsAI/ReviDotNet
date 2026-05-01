// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;

namespace Revi;

/// <summary>
/// Parses the [[_loop]] raw section of a .agent file into a list of LoopNode objects.
///
/// DSL format:
///   search                                    # non-indented: state declaration
///     -> analyze [when: ANALYSIS_READY]       # indented: transition with signal condition
///     -> search  [when: CONTINUE]             # self-loop
///     -> [end]   [when: ABORT]                # terminate
///   analyze
///     -> report  [when: DONE]
///     -> [end]                                # unconditional (no [when:] = always)
/// </summary>
public static class LoopDslParser
{
    // Matches: -> <target> [when: SIGNAL]  OR  -> <target>
    // State name pattern: leading word char, followed by word chars or hyphens.
    // Allows conventional names like "resolve-conflict" and "pick-relevant".
    private static readonly Regex TransitionRegex = new(
        @"^\s*->\s*(?<target>\[end\]|self|\w[\w-]*)\s*(?:\[when:\s*(?<signal>[A-Z0-9_]+)\])?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a [[_loop]] raw block into a list of LoopNode objects.
    /// </summary>
    /// <param name="dslText">The raw text content of the [[_loop]] section.</param>
    /// <returns>List of LoopNode objects describing the state graph.</returns>
    public static List<LoopNode> Parse(string dslText)
    {
        var nodes = new List<LoopNode>();
        LoopNode? currentNode = null;

        foreach (var rawLine in dslText.Split('\n'))
        {
            string line = rawLine.TrimEnd();

            // Skip blank lines and comment lines
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            // Strip inline comments
            int commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
                line = line[..commentIdx].TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var transitionMatch = TransitionRegex.Match(line);
            if (transitionMatch.Success)
            {
                // This is a transition line (starts with optional whitespace then ->)
                if (currentNode == null)
                {
                    Util.Log("LoopDslParser: Transition found before any state declaration. Skipping.");
                    continue;
                }

                string target = transitionMatch.Groups["target"].Value.Trim();
                string? signal = transitionMatch.Groups["signal"].Success
                    ? transitionMatch.Groups["signal"].Value.Trim().ToUpperInvariant()
                    : null;

                currentNode.Transitions.Add(new LoopTransition
                {
                    TargetState = target,
                    Signal = signal
                });
            }
            else if (!line.StartsWith(' ') && !line.StartsWith('\t'))
            {
                // Non-indented, non-arrow line: state name declaration
                string stateName = line.Trim();
                if (string.IsNullOrWhiteSpace(stateName))
                    continue;

                currentNode = new LoopNode { StateName = stateName };
                nodes.Add(currentNode);
            }
            // else: indented non-arrow line — ignored (could be comments or future syntax)
        }

        return nodes;
    }
}
