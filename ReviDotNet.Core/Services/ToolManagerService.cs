// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Service implementation of <see cref="IToolManager"/>. Holds built-in and custom tools as instance state.
/// Built-in tools are registered at construction time. <see cref="InvokeAgentTool"/> is registered with a
/// <see cref="Lazy{T}"/> reference to <see cref="IAgentService"/> to avoid a circular DI dependency.
/// </summary>
public sealed class ToolManagerService : IToolManager
{
    private readonly Dictionary<string, IBuiltInTool> _builtIns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToolProfile> _customTools = [];
    private readonly IReviLogger<ToolManagerService> _logger;

    /// <summary>Initializes a new <see cref="ToolManagerService"/> and registers the default built-in tools.</summary>
    public ToolManagerService(Lazy<IAgentService> agentService, IReviLogger<ToolManagerService> logger)
    {
        _logger = logger;

        Register(new WebSearchTool());
        Register(new WebScrapeTool());
        Register(new InvokeAgentTool(agentService));
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        _customTools.Clear();

        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Tools/";

        try
        {
            LoadFromFileSystem(path);
        }
        catch (DirectoryNotFoundException)
        {
            LoadFromEmbeddedResources(assembly);
        }
        catch (Exception e)
        {
            _logger.LogError($"ToolManager: Error loading tools: {e.Message}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Register(IBuiltInTool tool)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new ArgumentException("Tool name must not be null or empty.", nameof(tool));

        if (_builtIns.ContainsKey(tool.Name))
            _logger.LogInfo($"ToolManager: Overwriting existing built-in tool \"{tool.Name}\".");

        _builtIns[tool.Name] = tool;
    }

    /// <inheritdoc/>
    public bool Unregister(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _builtIns.Remove(name);
    }

    /// <inheritdoc/>
    public IBuiltInTool? GetBuiltIn(string name)
        => _builtIns.TryGetValue(name, out IBuiltInTool? tool) ? tool : null;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> GetBuiltInNames()
        => _builtIns.Keys.ToList();

    /// <inheritdoc/>
    public ToolProfile? GetCustom(string name)
        => _customTools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public List<ToolProfile> GetAllCustom()
        => [.._customTools];

    private void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.tool", SearchOption.AllDirectories)
            .ToList();

        foreach (string file in files)
        {
            try
            {
                Dictionary<string, string> data = RConfigParser.Read(file);
                ToolProfile? tool = RConfigParser.ToObject<ToolProfile>(data);

                if (tool?.Name is null || !tool.Enabled)
                    continue;

                if (data.TryGetValue("mcp_capabilities", out string? caps))
                    tool.Capabilities = Util.SplitByCommaOrSpace(caps);

                CheckAdd(tool, embedded: false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"ToolManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    private void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            if (assembly is null) return;

            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Tools.") &&
                            n.EndsWith(".tool", StringComparison.InvariantCultureIgnoreCase));

            foreach (string resourceName in resourceNames)
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;

                    using StreamReader reader = new(stream);
                    Dictionary<string, string> data = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                    ToolProfile? tool = RConfigParser.ToObject<ToolProfile>(data);

                    if (tool?.Name is null || !tool.Enabled)
                        continue;

                    if (data.TryGetValue("mcp_capabilities", out string? caps))
                        tool.Capabilities = Util.SplitByCommaOrSpace(caps);

                    CheckAdd(tool, embedded: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"ToolManager: Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"ToolManager: Error loading from embedded resources: {e.Message}");
        }
    }

    private void CheckAdd(ToolProfile tool, bool embedded)
    {
        if (_customTools.Any(t => t.Name == tool.Name))
        {
            _logger.LogInfo($"ToolManager: Duplicate tool name '{tool.Name}' — skipping.");
            return;
        }

        _customTools.Add(tool);
        _logger.LogInfo(embedded
            ? $"ToolManager: Loaded embedded tool \"{tool.Name}\""
            : $"ToolManager: Loaded tool \"{tool.Name}\" from file system");
    }
}
