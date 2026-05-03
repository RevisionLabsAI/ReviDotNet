// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>Service implementation of <see cref="IAgentManager"/>. Holds loaded agent profiles as instance state.</summary>
public sealed class AgentManagerService : IAgentManager
{
    private readonly List<AgentProfile> _agents = [];
    private readonly IReviLogger<AgentManagerService> _logger;

    /// <summary>Initializes a new <see cref="AgentManagerService"/>.</summary>
    public AgentManagerService(IReviLogger<AgentManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
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
            _logger.LogError($"AgentManager: Error loading agents: {e.Message}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public AgentProfile? Get(string name)
        => _agents.FirstOrDefault(a => a.Name == name);

    /// <inheritdoc/>
    public List<AgentProfile> GetAll()
        => [.._agents];

    /// <inheritdoc/>
    public void Add(AgentProfile agent)
        => _agents.Add(agent);

    private void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.agent", SearchOption.AllDirectories)
            .ToList();

        foreach (string file in files)
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
                _logger.LogError($"AgentManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    private void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Agents.") &&
                            n.EndsWith(".agent", StringComparison.InvariantCultureIgnoreCase));

            foreach (string resourceName in resourceNames)
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;

                    using StreamReader reader = new(stream);
                    Dictionary<string, string> data = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                    string folder = Util.ExtractEmbeddedDirectories(".Agents.", resourceName).ToLower();
                    AgentProfile agent = AgentProfile.ToObject(data, folder);

                    if (agent?.Name is null)
                        continue;

                    CheckAdd(agent, embedded: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"AgentManager: Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"AgentManager: Error loading from embedded resources: {e.Message}");
        }
    }

    private void CheckAdd(AgentProfile agent, bool embedded)
    {
        if (_agents.Any(a => a.Name == agent.Name))
        {
            _logger.LogInfo($"AgentManager: Duplicate agent name '{agent.Name}' — skipping.");
            return;
        }

        _agents.Add(agent);
        _logger.LogInfo(embedded
            ? $"AgentManager: Loaded embedded agent \"{agent.Name}\""
            : $"AgentManager: Loaded agent \"{agent.Name}\" from file system");
    }
}
