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
internal static class Agent
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
    /// Delegates to the DI-registered <see cref="IAgentService"/> when running inside a host;
    /// falls back to direct <see cref="AgentRunner"/> construction (test / standalone path).
    /// </summary>
    public static async Task<AgentResult> Run(
        string agentName,
        Dictionary<string, object>? inputs,
        AgentRunContext ctx,
        CancellationToken token = default)
    {
        if (ReviServiceLocator.TryGetService<IAgentService>(out IAgentService? svc) && svc != null)
            return await svc.Run(agentName, inputs, ctx, token);

        // Standalone / test path: resolve registries from the static managers directly.
        AgentProfile profile = FindAgent(agentName);
        AgentRunner runner = new(
            profile,
            inputs ?? new Dictionary<string, object>(),
            token,
            ctx,
            ReviServiceLocator.TryGetService<IModelManager>(out IModelManager? mm) ? mm! : new StaticModelAdapter(),
            ReviServiceLocator.TryGetService<IPromptManager>(out IPromptManager? pm) ? pm! : new StaticPromptAdapter(),
            ReviServiceLocator.TryGetService<IToolManager>(out IToolManager? tm) ? tm! : new StaticToolAdapter());
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
        AgentResult result = await Run(agentName, inputs, token);
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
        AgentResult result = await Run(agentName, input, token);
        return result.ExitReason == AgentExitReason.Completed ? result.FinalOutput : null;
    }

    /// <summary>
    /// Finds a registered agent by name. Throws if not found.
    /// </summary>
    public static AgentProfile FindAgent(string name)
    {
        AgentProfile? agent = AgentManager.Get(name);
        if (agent == null)
            throw new Exception($"Agent '{name}' not found. Ensure the .agent file is in RConfigs/Agents/ and is registered.");
        return agent;
    }

    // Minimal adapters for the standalone / test path only — not used when DI is configured.

    private sealed class StaticModelAdapter : IModelManager
    {
        public List<ModelProfile> GetAll() => ModelManager.GetAll();
        public ModelProfile? Get(string name) => ModelManager.Get(name);
        public ModelProfile? Find(string? minTier, bool needsPromptCompletion = false) => ModelManager.Find(minTier, needsPromptCompletion);
        public ModelProfile? Find(string? minTier, bool needsPromptCompletion, List<string>? blockedModels) => ModelManager.Find(minTier, needsPromptCompletion, blockedModels);
        public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false) => ModelManager.Find(minTier, needsPromptCompletion);
        public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion, List<string>? blockedModels) => ModelManager.Find(minTier, needsPromptCompletion, blockedModels);
        public void Add(ModelProfile model) => ModelManager.Add(model);
        public Task LoadAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default) { ModelManager.Load(assembly); return Task.CompletedTask; }
    }

    private sealed class StaticPromptAdapter : IPromptManager
    {
        public Prompt? Get(string name) => PromptManager.Get(name);
        public List<Prompt> GetAll() => PromptManager.GetAll();
        public void AddOrUpdate(Prompt prompt) => PromptManager.AddOrUpdate(prompt);
        public void LoadFromFile(string filePath) => PromptManager.LoadFromFile(filePath);
        public Task LoadAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default) { PromptManager.Load(assembly); return Task.CompletedTask; }
    }

    private sealed class StaticToolAdapter : IToolManager
    {
        public IBuiltInTool? GetBuiltIn(string name) => ToolManager.GetBuiltIn(name);
        public IReadOnlyCollection<string> GetBuiltInNames() => ToolManager.GetBuiltInNames();
        public ToolProfile? GetCustom(string name) => ToolManager.GetCustom(name);
        public List<ToolProfile> GetAllCustom() => ToolManager.GetAllCustom();
        public void Register(IBuiltInTool tool) => ToolManager.Register(tool);
        public bool Unregister(string name) => ToolManager.Unregister(name);
        public Task LoadAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default) { ToolManager.Load(assembly); return Task.CompletedTask; }
    }
}
