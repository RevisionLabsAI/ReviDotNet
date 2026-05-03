// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI service implementation of <see cref="IAgentService"/>. Replaces the static <c>Agent</c> class.
/// Creates <see cref="AgentRunner"/> instances with injected registry services.
/// </summary>
public sealed class AgentService(
    IAgentManager agents,
    IModelManager models,
    IPromptManager prompts,
    IToolManager tools,
    IReviLogger<AgentService> logger) : IAgentService
{
    /// <inheritdoc/>
    public Task<AgentResult> Run(
        string agentName,
        Dictionary<string, object>? inputs = null,
        CancellationToken token = default)
        => Run(agentName, inputs, AgentRunContext.Root(), token);

    /// <inheritdoc/>
    public async Task<AgentResult> Run(
        string agentName,
        Dictionary<string, object>? inputs,
        AgentRunContext ctx,
        CancellationToken token = default)
    {
        AgentProfile profile = FindAgent(agentName);
        AgentRunner runner = new(profile, inputs ?? new Dictionary<string, object>(), token, ctx, models, prompts, tools);
        return await runner.RunAsync();
    }

    /// <inheritdoc/>
    public Task<AgentResult> Run(
        string agentName,
        string input,
        CancellationToken token = default)
        => Run(agentName, new Dictionary<string, object> { ["input"] = input }, token);

    /// <inheritdoc/>
    public async Task<string?> ToString(
        string agentName,
        Dictionary<string, object>? inputs = null,
        CancellationToken token = default)
    {
        AgentResult result = await Run(agentName, inputs, token);
        return result.ExitReason == AgentExitReason.Completed ? result.FinalOutput : null;
    }

    /// <inheritdoc/>
    public async Task<string?> ToString(
        string agentName,
        string input,
        CancellationToken token = default)
    {
        AgentResult result = await Run(agentName, input, token);
        return result.ExitReason == AgentExitReason.Completed ? result.FinalOutput : null;
    }

    private AgentProfile FindAgent(string name)
    {
        AgentProfile? agent = agents.Get(name);
        if (agent == null)
            throw new Exception($"Agent '{name}' not found. Ensure the .agent file is in RConfigs/Agents/ and is registered.");
        return agent;
    }
}
