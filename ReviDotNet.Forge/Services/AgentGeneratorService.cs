// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Uses AI to generate a .agent file from a description of purpose, tools, and I/O.
/// Mirrors <see cref="PromptGeneratorService"/> for the agent workflow.
/// </summary>
public class AgentGeneratorService
{
    private readonly string _agentsSourcePath;
    private readonly IInferService _infer;
    private readonly IPromptManager _prompts;
    private readonly IAgentManager _agents;
    private readonly IToolManager _tools;
    private readonly ArtifactHistoryService? _history;

    public AgentGeneratorService(
        IConfiguration configuration,
        IInferService infer,
        IPromptManager prompts,
        IAgentManager agents,
        IToolManager tools,
        ArtifactHistoryService? history = null)
    {
        _agentsSourcePath = configuration["Forge:AgentsSourcePath"] ?? "RConfigs/Agents";
        _infer = infer;
        _prompts = prompts;
        _agents = agents;
        _tools = tools;
        _history = history;
    }

    /// <summary>
    /// Streams a generated .agent file from the AI given a description, available tools,
    /// and an expected I/O description. The caller assembles the streamed tokens.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string agentName,
        string purpose,
        IReadOnlyCollection<string> selectedTools,
        string ioDescription,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string toolsText = selectedTools is { Count: > 0 }
            ? string.Join(", ", selectedTools)
            : "(none)";

        var inputs = new List<Input>
        {
            new("Agent Name", agentName),
            new("Purpose", purpose),
            new("Available Tools", toolsText),
            new("Expected IO", string.IsNullOrWhiteSpace(ioDescription) ? "(not specified)" : ioDescription)
        };

        Prompt? generatorPrompt = _prompts.Get("AgentWorkshop.Generator")
            ?? throw new InvalidOperationException(
                "AgentWorkshop.Generator prompt not found. Ensure it exists in RConfigs/Prompts/AgentWorkshop/.");

        await foreach (string token in _infer.CompletionStream(generatorPrompt, inputs).WithCancellation(ct))
            yield return token;
    }

    /// <summary>
    /// Returns the list of tool names available to be passed into a generated agent.
    /// Combines registered built-in tools and custom tool profiles.
    /// </summary>
    public List<string> ListAvailableTools()
    {
        var names = new List<string>();
        names.AddRange(_tools.GetBuiltInNames());
        names.AddRange(_tools.GetAllCustom().Select(t => t.Name ?? string.Empty).Where(n => !string.IsNullOrEmpty(n)));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
    }

    /// <summary>
    /// Writes a new .agent file under the configured source path and reloads the
    /// in-memory agent registry so subsequent runs pick it up.
    ///
    /// The <paramref name="agentName"/> may use '/' to indicate a sub-folder
    /// (matching AgentManager's loading convention, where the folder name becomes
    /// a name prefix). Example: "test/router" → RConfigs/Agents/test/router.agent.
    /// </summary>
    public async Task SaveNewAsync(string agentName, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            throw new ArgumentException("Agent name is required.", nameof(agentName));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Agent content is required.", nameof(content));

        string relative = agentName.Replace('/', Path.DirectorySeparatorChar) + ".agent";
        string fullPath = Path.Combine(_agentsSourcePath, relative);

        // Snapshot the existing content for version history before overwriting.
        if (_history is not null && File.Exists(fullPath))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(fullPath, ct);
                _history.Snapshot("agent", agentName, existing);
            }
            catch { /* best-effort */ }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);

        // Reload agent registry so the new agent is immediately available in the UI.
        await _agents.LoadAsync(Assembly.GetExecutingAssembly(), ct);
    }
}
