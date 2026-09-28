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
/// Custom-tool reads never lock and are safe during a reload: every write builds a complete replacement list and
/// publishes it with one reference swap, so a reader sees either the old list or the new one, never a partial one.
/// </summary>
public sealed class ToolManagerService : IToolManager
{
    private readonly Dictionary<string, IBuiltInTool> _builtIns = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Serialises custom-tool writers so concurrent loads cannot lose each other's entries. Readers never take it.</summary>
    private readonly object _writeLock = new();
    /// <summary>The published custom tools. Never mutated once assigned; writers replace the whole array under <see cref="_writeLock"/>.</summary>
    private volatile ToolProfile[] _customTools = [];
    private readonly IReviLogger<ToolManagerService> _logger;

    /// <summary>Initializes a new <see cref="ToolManagerService"/> and registers the default built-in tools.</summary>
    public ToolManagerService(Lazy<IAgentService> agentService, IWebContentService webContent, IModelManager models, IReviLogger<ToolManagerService> logger,
        IDocumentSearchService? documentSearch = null, IDocumentTextExtractor? textExtractor = null)
    {
        _logger = logger;

        Register(new WebSearchTool());
        Register(new ToolSearchTool());
        Register(new WebScrapeTool(webContent));
        Register(new WebExtractTool(webContent));
        Register(new InvokeAgentTool(agentService));

        // File-access tools (operate on AgentRunContext.Files; the reader needs the model registry). Every file
        // tool shares the host's registered extractor, so a host PDF extractor serves read-file too.
        IDocumentTextExtractor extractor = textExtractor ?? new DocumentTextExtractor();
        Register(new ListFilesTool());
        Register(new ReadFileTool(models, extractor));
        if (documentSearch is not null)
        {
            Register(new DocumentSearchTool(documentSearch, extractor));
            Register(new DocumentSearchTool(documentSearch, extractor, "search-files"));
        }
        else Register(new SearchFilesTool(models, extractor));
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Tools/";

        lock (_writeLock)
        {
            List<ToolProfile> loaded = [];
            try
            {
                ReadFileSystem(path, loaded);
            }
            catch (DirectoryNotFoundException)
            {
                ReadEmbeddedResources(assembly, loaded);
            }
            catch (Exception e)
            {
                _logger.LogError($"ToolManager: Error loading tools: {e.Message}");
            }

            _customTools = [.. loaded];
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void LoadDirectory(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "Tools") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(path)) return;
        LoadFromFileSystem(path);
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

    /// <summary>Additively loads the <c>.tool</c> files under <paramref name="path"/>; existing tools win a name clash.</summary>
    /// <param name="path">The directory to read.</param>
    private void LoadFromFileSystem(string path) => Merge(loaded => ReadFileSystem(path, loaded));

    /// <summary>Publishes the current custom tools plus whatever <paramref name="read"/> appends, as one swap.</summary>
    /// <param name="read">Appends newly read tools to the working copy it is given.</param>
    private void Merge(Action<List<ToolProfile>> read)
    {
        lock (_writeLock)
        {
            List<ToolProfile> merged = [.. _customTools];
            read(merged);
            _customTools = [.. merged];
        }
    }

    /// <summary>Reads the <c>.tool</c> files under <paramref name="path"/> into <paramref name="target"/>.</summary>
    /// <param name="path">The directory to read; a missing one throws <see cref="DirectoryNotFoundException"/>.</param>
    /// <param name="target">The working list new tools are appended to.</param>
    private void ReadFileSystem(string path, List<ToolProfile> target)
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

                CheckAdd(target, tool, embedded: false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"ToolManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public void LoadAssembly(Assembly assembly) => Merge(loaded => ReadEmbeddedResources(assembly, loaded));

    /// <summary>Reads the tool profiles embedded in <paramref name="assembly"/> into <paramref name="target"/>.</summary>
    /// <param name="assembly">The assembly whose <c>.Tools.</c> resources are read; null reads nothing.</param>
    /// <param name="target">The working list new tools are appended to.</param>
    private void ReadEmbeddedResources(Assembly assembly, List<ToolProfile> target)
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

                    CheckAdd(target, tool, embedded: true);
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

    /// <summary>Appends <paramref name="tool"/> to <paramref name="target"/> unless a tool of that name is already there.</summary>
    /// <param name="target">The working list being built.</param>
    /// <param name="tool">The tool just read.</param>
    /// <param name="embedded">Whether it came from an embedded resource (for the log line).</param>
    private void CheckAdd(List<ToolProfile> target, ToolProfile tool, bool embedded)
    {
        if (target.Any(t => t.Name == tool.Name))
        {
            _logger.LogInfo($"ToolManager: Duplicate tool name '{tool.Name}' — skipping.");
            return;
        }

        target.Add(tool);
        _logger.LogInfo(embedded
            ? $"ToolManager: Loaded embedded tool \"{tool.Name}\""
            : $"ToolManager: Loaded tool \"{tool.Name}\" from file system");
    }
}
