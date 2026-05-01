// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Static registry for both built-in tools and custom tool profiles loaded from .tool rconfig files.
/// Built-in tools are registered in the static constructor and always available.
/// Custom tools are loaded from: RConfigs/Tools/**/*.tool
/// </summary>
public static class ToolManager
{
    // ==============
    //  Declarations
    // ==============

    private static readonly Dictionary<string, IBuiltInTool> _builtIns = new(StringComparer.OrdinalIgnoreCase);
    private static List<ToolProfile> _customTools = new();


    // ==================
    //  Static Constructor (registers built-in tools)
    // ==================

    static ToolManager()
    {
        Register(new WebSearchTool());
        Register(new WebScrapeTool());
        Register(new InvokeAgentTool());
    }

    /// <summary>
    /// Registers a built-in tool. If a tool with the same name already exists it is overwritten,
    /// and the replacement is logged. Intended to be called during host startup, before any
    /// <see cref="Agent.Run(string, Dictionary{string, object}?, CancellationToken)"/> calls —
    /// concurrent registration during agent execution is not synchronized.
    /// </summary>
    public static void Register(IBuiltInTool tool)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new ArgumentException("Tool name must not be null or empty.", nameof(tool));

        if (_builtIns.ContainsKey(tool.Name))
            Util.Log($"ToolManager: Overwriting existing built-in tool \"{tool.Name}\".");

        _builtIns[tool.Name] = tool;
    }

    /// <summary>
    /// Removes a built-in tool by name. Returns true if a tool was removed, false otherwise.
    /// Primarily intended for tests that need to swap stub tools in and out.
    /// </summary>
    public static bool Unregister(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _builtIns.Remove(name);
    }


    // ==================
    //  Tool Loading
    // ==================

    #region Tool Loading

    /// <summary>
    /// Clears existing custom tools and loads tool profiles from the default path.
    /// Built-in tools are never cleared.
    /// </summary>
    public static void Load(Assembly assembly = null)
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
            Util.Log($"ToolManager: Error loading tools: {e.Message}");
        }
    }

    private static void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.tool", SearchOption.AllDirectories)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                Dictionary<string, string> data = RConfigParser.Read(file);
                // Parse capabilities separately since it's a comma-separated list not directly handled by ToObject<T>
                ToolProfile? tool = RConfigParser.ToObject<ToolProfile>(data);

                if (tool?.Name is null || !tool.Enabled)
                    continue;

                // Parse capabilities from the raw value
                if (data.TryGetValue("mcp_capabilities", out var caps))
                    tool.Capabilities = Util.SplitByCommaOrSpace(caps);

                CheckAdd(tool, embedded: false);
            }
            catch (Exception ex)
            {
                Util.Log($"ToolManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    private static void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            if (assembly is null)
                return;

            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Tools.") &&
                               name.EndsWith(".tool", StringComparison.InvariantCultureIgnoreCase));

            foreach (var resourceName in resourceNames)
            {
                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;

                    using var reader = new StreamReader(stream);
                    var data = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                    ToolProfile? tool = RConfigParser.ToObject<ToolProfile>(data);

                    if (tool?.Name is null || !tool.Enabled)
                        continue;

                    if (data.TryGetValue("mcp_capabilities", out var caps))
                        tool.Capabilities = Util.SplitByCommaOrSpace(caps);

                    CheckAdd(tool, embedded: true);
                }
                catch (Exception ex)
                {
                    Util.Log($"ToolManager: Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Util.Log($"ToolManager: Error loading from embedded resources: {e.Message}");
        }
    }

    #endregion


    // ======================
    //  Supporting Functions
    // ======================

    private static void CheckAdd(ToolProfile tool, bool embedded)
    {
        var existing = _customTools.FirstOrDefault(t => t.Name == tool.Name);
        if (existing == null)
        {
            _customTools.Add(tool);
            if (embedded)
                Util.Log($"ToolManager: Loaded embedded tool \"{tool.Name}\"");
            else
                Util.Log($"ToolManager: Loaded tool \"{tool.Name}\" from file system");
        }
        else
        {
            Util.Log($"ToolManager: Duplicate tool name '{tool.Name}' — skipping.");
        }
    }


    // ===============
    //  Accessibility
    // ===============

    #region Accessibility

    /// <summary>Returns the built-in tool with the given name, or null.</summary>
    public static IBuiltInTool? GetBuiltIn(string name)
        => _builtIns.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>Returns the custom (MCP/HTTP) tool profile with the given name, or null.</summary>
    public static ToolProfile? GetCustom(string name)
        => _customTools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns all registered built-in tool names.</summary>
    public static IReadOnlyCollection<string> GetBuiltInNames()
        => _builtIns.Keys.ToList();

    /// <summary>Returns all loaded custom tool profiles.</summary>
    public static List<ToolProfile> GetAllCustom()
        => _customTools.ToList();

    #endregion
}
