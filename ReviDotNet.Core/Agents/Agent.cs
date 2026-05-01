// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Static entry-point for running agent loops. Mirrors the Infer class for single-shot LLM calls.
///
/// Usage:
///   AgentResult result = await Agent.Run("research/research-agent", inputs, token);
///   string? output = await Agent.ToString("research/research-agent", "my query", token);
/// </summary>
public static class Agent
{
    /// <summary>
    /// Runs a registered agent by name with the given named inputs.
    /// </summary>
    /// <param name="agentName">
    /// The logical name of the agent as declared in [[information]] name = ... of the .agent file.
    /// Includes subdirectory prefix if nested (e.g. "research/my-agent").
    /// </param>
    /// <param name="inputs">Named inputs provided to the agent at the start of the run.</param>
    /// <param name="token">Cancellation token for the entire run.</param>
    /// <returns>An AgentResult describing what happened and the final output.</returns>
    /// <exception cref="Exception">Thrown if the agent name is not found in AgentManager.</exception>
    public static Task<AgentResult> Run(
        string agentName,
        Dictionary<string, object>? inputs = null,
        CancellationToken token = default)
        => Run(agentName, inputs, AgentRunContext.Root(), token);

    /// <summary>
    /// Runs a registered agent with explicit run context. Used by InvokeAgentTool to nest
    /// a sub-agent's ReviLog tree under the parent agent's tool-call event.
    /// </summary>
    public static async Task<AgentResult> Run(
        string agentName,
        Dictionary<string, object>? inputs,
        AgentRunContext ctx,
        CancellationToken token = default)
    {
        AgentProfile profile = FindAgent(agentName);
        var runner = new AgentRunner(profile, inputs ?? new Dictionary<string, object>(), token, ctx);
        return await runner.RunAsync();
    }

    /// <summary>
    /// Runs an agent by name with a single string input (convenience overload).
    /// The input is passed as key "input".
    /// </summary>
    public static Task<AgentResult> Run(
        string agentName,
        string input,
        CancellationToken token = default)
        => Run(agentName, new Dictionary<string, object> { ["input"] = input }, token);

    /// <summary>
    /// Runs an agent and returns only the final output string, or null if the agent
    /// did not complete successfully.
    /// </summary>
    public static async Task<string?> ToString(
        string agentName,
        Dictionary<string, object>? inputs = null,
        CancellationToken token = default)
    {
        var result = await Run(agentName, inputs, token);
        return result.ExitReason == AgentExitReason.Completed ? result.FinalOutput : null;
    }

    /// <summary>
    /// Runs an agent with a single string input and returns only the final output string.
    /// </summary>
    public static async Task<string?> ToString(
        string agentName,
        string input,
        CancellationToken token = default)
    {
        var result = await Run(agentName, input, token);
        return result.ExitReason == AgentExitReason.Completed ? result.FinalOutput : null;
    }

    /// <summary>
    /// Finds a registered agent by name. Throws if not found.
    /// </summary>
    public static AgentProfile FindAgent(string name)
    {
        var agent = AgentManager.Get(name);
        if (agent == null)
            throw new Exception($"Agent '{name}' not found. Ensure the .agent file is in RConfigs/Agents/ and is registered.");
        return agent;
    }
}
