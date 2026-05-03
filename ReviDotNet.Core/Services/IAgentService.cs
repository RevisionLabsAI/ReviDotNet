// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI interface for agent execution. Replaces the static <c>Agent</c> class.
/// Inject as <c>IAgentService agent</c> for clean call sites: <c>agent.Run(...)</c>.
/// </summary>
public interface IAgentService
{
    /// <summary>Runs a registered agent by name with named inputs.</summary>
    Task<AgentResult> Run(
        string agentName,
        Dictionary<string, object>? inputs = null,
        CancellationToken token = default);

    /// <summary>Runs a registered agent with explicit run context (used by sub-agent nesting).</summary>
    Task<AgentResult> Run(
        string agentName,
        Dictionary<string, object>? inputs,
        AgentRunContext ctx,
        CancellationToken token = default);

    /// <summary>Convenience overload: passes a single string as inputs["input"].</summary>
    Task<AgentResult> Run(
        string agentName,
        string input,
        CancellationToken token = default);

    /// <summary>Runs an agent and returns only the final output string, or null on failure.</summary>
    Task<string?> ToString(
        string agentName,
        Dictionary<string, object>? inputs = null,
        CancellationToken token = default);

    /// <summary>Runs an agent with a single string input and returns only the final output.</summary>
    Task<string?> ToString(
        string agentName,
        string input,
        CancellationToken token = default);
}
