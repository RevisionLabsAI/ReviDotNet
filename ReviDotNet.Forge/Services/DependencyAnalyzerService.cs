// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Walks the loaded agent registry and surfaces dependencies — which prompts each
/// agent state references, and (in reverse) which agents reference a given prompt.
/// Drives the "Bindings" and "Used by" sections in the registry detail drawers.
///
/// The analyzer is intentionally lightweight: it inspects already-parsed
/// <see cref="AgentProfile.States"/> rather than re-parsing the .agent source.
/// Tool references and prompt references each form a distinct surface so the UI
/// can render them separately.
/// </summary>
public sealed class DependencyAnalyzerService
{
    private readonly IAgentManager _agents;

    public DependencyAnalyzerService(IAgentManager agents)
    {
        _agents = agents;
    }

    /// <summary>
    /// Returns the set of prompt names this agent references across all its states.
    /// Empty if the agent only uses the default prompt (state.Prompt unset).
    /// </summary>
    public List<string> PromptsUsedBy(string agentName)
    {
        var agent = _agents.Get(agentName);
        if (agent is null) return new();
        return agent.States
            .Select(s => s.Prompt)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();
    }

    /// <summary>
    /// Returns the set of tool names this agent references across all its states.
    /// </summary>
    public List<string> ToolsUsedBy(string agentName)
    {
        var agent = _agents.Get(agentName);
        if (agent is null) return new();
        return agent.States
            .SelectMany(s => s.Tools ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();
    }

    /// <summary>
    /// Reverse lookup: which agents reference this prompt name in any of their states?
    /// </summary>
    public List<string> AgentsReferencing(string promptName)
    {
        if (string.IsNullOrWhiteSpace(promptName)) return new();
        return _agents.GetAll()
            .Where(a => a.States.Any(s => string.Equals(s.Prompt, promptName, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Name ?? string.Empty)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n)
            .ToList();
    }
}
