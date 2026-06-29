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

    /// <summary>
    /// Runs the <paramref name="profile"/> directly without an <see cref="IAgentManager"/> name
    /// lookup, so no shared registry slot is mutated. Intended for per-run isolation (e.g. the
    /// Refinery evaluating candidate agents concurrently):
    /// <list type="bullet">
    ///   <item><paramref name="toolOverride"/>, when non-null, is used as the run's tool registry
    ///         instead of the injected <see cref="IToolManager"/> — giving each run a private,
    ///         non-shared tool set.</item>
    ///   <item><paramref name="modelOverride"/>, when non-null, is used as the model for every
    ///         LLM call instead of resolving per-state from the model registry.</item>
    /// </list>
    /// The existing name-based <see cref="Run(string, Dictionary{string, object}, AgentRunContext, CancellationToken)"/>
    /// overload and its behaviour are unchanged.
    /// </summary>
    Task<AgentResult> Run(
        AgentProfile profile,
        IReadOnlyDictionary<string, object> inputs,
        AgentRunContext? context = null,
        CancellationToken token = default,
        IToolManager? toolOverride = null,
        ModelProfile? modelOverride = null);

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
