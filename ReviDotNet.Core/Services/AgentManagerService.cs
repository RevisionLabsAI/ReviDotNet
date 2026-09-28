// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Service implementation of <see cref="IAgentManager"/>. Holds loaded agent profiles as instance state.
/// Reads never lock and are safe during a reload: every write builds a complete replacement list and publishes it
/// with one reference swap, so a reader sees either the old list or the new one, never a partial or empty one.
/// </summary>
public sealed class AgentManagerService : IAgentManager
{
    /// <summary>Serialises writers so concurrent loads and adds cannot lose each other's entries. Readers never take it.</summary>
    private readonly object _writeLock = new();
    /// <summary>The published agents. Never mutated once assigned; writers replace the whole array under <see cref="_writeLock"/>.</summary>
    private volatile AgentProfile[] _agents = [];
    private readonly IReviLogger<AgentManagerService> _logger;

    /// <summary>Initializes a new <see cref="AgentManagerService"/>.</summary>
    public AgentManagerService(IReviLogger<AgentManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Agents/";

        lock (_writeLock)
        {
            List<AgentProfile> loaded = [];
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
                _logger.LogError($"AgentManager: Error loading agents: {e.Message}");
            }

            _agents = [.. loaded];
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void LoadDirectory(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "Agents") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(path)) return;
        LoadFromFileSystem(path);
    }

    /// <inheritdoc/>
    public AgentProfile? Get(string name)
        => _agents.FirstOrDefault(a => a.Name == name);

    /// <inheritdoc/>
    public List<AgentProfile> GetAll()
        => [.._agents];

    /// <inheritdoc/>
    public void Add(AgentProfile agent)
    {
        lock (_writeLock) _agents = [.. _agents, agent];
    }

    /// <inheritdoc/>
    public void AddOrReplace(AgentProfile agent)
    {
        lock (_writeLock) _agents = [.. _agents.Where(a => a.Name != agent.Name), agent];
    }

    /// <summary>Additively loads the <c>.agent</c> files under <paramref name="path"/>; existing agents win a name clash.</summary>
    /// <param name="path">The directory to read.</param>
    private void LoadFromFileSystem(string path) => Merge(loaded => ReadFileSystem(path, loaded));

    /// <summary>Publishes the current agents plus whatever <paramref name="read"/> appends, as one swap.</summary>
    /// <param name="read">Appends newly read agents to the working copy it is given.</param>
    private void Merge(Action<List<AgentProfile>> read)
    {
        lock (_writeLock)
        {
            List<AgentProfile> merged = [.. _agents];
            read(merged);
            _agents = [.. merged];
        }
    }

    /// <summary>Reads the <c>.agent</c> files under <paramref name="path"/> into <paramref name="target"/>.</summary>
    /// <param name="path">The directory to read; a missing one throws <see cref="DirectoryNotFoundException"/>.</param>
    /// <param name="target">The working list new agents are appended to.</param>
    private void ReadFileSystem(string path, List<AgentProfile> target)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.agent", SearchOption.AllDirectories)
            .ToList();

        foreach (string file in files)
        {
            try
            {
                Dictionary<string, string> data = RConfigParser.Read(file);
                // Subfolders are organizational only; the declared name is the registered name.
                AgentProfile agent = AgentProfile.ToObject(data);

                if (agent?.Name is null)
                    continue;

                // Stamp the originating file so the source can be read back / written even when it lives
                // outside the app's own RConfigs (e.g. an additional RConfig folder).
                agent.SourcePath = Path.GetFullPath(file);

                CheckAdd(target, agent, embedded: false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"AgentManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public void LoadAssembly(Assembly assembly) => Merge(loaded => ReadEmbeddedResources(assembly, loaded));

    /// <summary>Reads the agent profiles embedded in <paramref name="assembly"/> into <paramref name="target"/>.</summary>
    /// <param name="assembly">The assembly whose <c>.Agents.</c> resources are read.</param>
    /// <param name="target">The working list new agents are appended to.</param>
    private void ReadEmbeddedResources(Assembly assembly, List<AgentProfile> target)
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
                    // Register under the declared name verbatim (no folder prefix): an embedded resource
                    // name cannot tell a folder dot from a dot inside a file name, and the declared name is
                    // what every lookup uses. See PromptRegistryNameTests.
                    AgentProfile agent = AgentProfile.ToObject(data);

                    if (agent?.Name is null)
                        continue;

                    CheckAdd(target, agent, embedded: true);
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

    /// <summary>Appends <paramref name="agent"/> to <paramref name="target"/> unless an agent of that name is already there.</summary>
    /// <param name="target">The working list being built.</param>
    /// <param name="agent">The agent just read.</param>
    /// <param name="embedded">Whether it came from an embedded resource (for the log line).</param>
    private void CheckAdd(List<AgentProfile> target, AgentProfile agent, bool embedded)
    {
        if (target.Any(a => a.Name == agent.Name))
        {
            _logger.LogInfo($"AgentManager: Duplicate agent name '{agent.Name}' — skipping.");
            return;
        }

        target.Add(agent);
        _logger.LogInfo(embedded
            ? $"AgentManager: Loaded embedded agent \"{agent.Name}\""
            : $"AgentManager: Loaded agent \"{agent.Name}\" from file system");
    }
}
