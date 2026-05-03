// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Static registry for agent profiles loaded from .agent rconfig files.
/// Mirrors ModelManager exactly in structure and behavior.
///
/// Agents are loaded from: RConfigs/Agents/**/*.agent
/// Embedded resource fallback: resources containing ".Agents." ending in ".agent"
/// </summary>
internal static class AgentManager
{
    // ==============
    //  Declarations
    // ==============

    private static List<AgentProfile> _agents = new();


    // ==================
    //  Agent Loading
    // ==================

    #region Agent Loading

    /// <summary>
    /// Clears existing agents and loads agent profiles from the default path.
    /// Falls back to embedded resources if the directory is not found.
    /// </summary>
    public static void Load(Assembly assembly = null)
    {
        _agents.Clear();

        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Agents/";

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
            Util.Log($"AgentManager: Error loading agents: {e.Message}");
        }
    }

    private static void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.agent", SearchOption.AllDirectories)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                Dictionary<string, string> data = RConfigParser.Read(file);
                string folder = Util.ExtractSubDirectories(path, file).ToLower();
                AgentProfile agent = AgentProfile.ToObject(data, folder);

                if (agent?.Name is null)
                    continue;

                CheckAdd(agent, embedded: false);
            }
            catch (Exception ex)
            {
                Util.Log($"AgentManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    private static void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            if (assembly is null)
                throw new Exception("Assembly cannot be null.");

            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Agents.") &&
                               name.EndsWith(".agent", StringComparison.InvariantCultureIgnoreCase));

            foreach (var resourceName in resourceNames)
            {
                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;

                    using var reader = new StreamReader(stream);
                    var data = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                    string folder = Util.ExtractEmbeddedDirectories(".Agents.", resourceName).ToLower();
                    AgentProfile agent = AgentProfile.ToObject(data, folder);

                    if (agent?.Name is null)
                        continue;

                    CheckAdd(agent, embedded: true);
                }
                catch (Exception ex)
                {
                    Util.Log($"AgentManager: Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Util.Log($"AgentManager: Error loading from embedded resources: {e.Message}");
        }
    }

    #endregion


    // ======================
    //  Supporting Functions
    // ======================

    #region Supporting Functions

    private static void CheckAdd(AgentProfile agent, bool embedded)
    {
        var existing = _agents.FirstOrDefault(a => a.Name == agent.Name);
        if (existing == null)
        {
            _agents.Add(agent);
            if (embedded)
                Util.Log($"AgentManager: Loaded embedded agent \"{agent.Name}\"");
            else
                Util.Log($"AgentManager: Loaded agent \"{agent.Name}\" from file system");
        }
        else
        {
            Util.Log($"AgentManager: Duplicate agent name '{agent.Name}' — skipping.");
        }
    }

    #endregion


    // ===============
    //  Accessibility
    // ===============

    #region Accessibility

    /// <summary>Returns the agent profile with the given name, or null if not found.</summary>
    public static AgentProfile? Get(string name)
        => _agents.FirstOrDefault(a => a.Name == name);

    /// <summary>Returns all loaded agent profiles.</summary>
    public static List<AgentProfile> GetAll()
        => _agents.ToList();

    /// <summary>Programmatically adds an agent profile to the registry.</summary>
    public static void Add(AgentProfile agent)
        => _agents.Add(agent);

    #endregion
}
