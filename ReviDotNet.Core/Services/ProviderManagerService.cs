// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Service implementation of <see cref="IProviderManager"/>. Holds loaded provider profiles as instance state.
/// Reads never lock and are safe during a reload: every write builds a complete replacement list and publishes it
/// with one reference swap, so a reader sees either the old list or the new one, never a partial or empty one.
/// </summary>
public sealed class ProviderManagerService : IProviderManager, IDisposable
{
    /// <summary>Serialises writers so concurrent loads and adds cannot lose each other's entries. Readers never take it.</summary>
    private readonly object _writeLock = new();
    /// <summary>The published providers. Never mutated once assigned; writers replace the whole array under <see cref="_writeLock"/>.</summary>
    private volatile ProviderProfile[] _providers = [];
    private readonly IReviLogger<ProviderManagerService> _logger;

    /// <summary>Initializes a new <see cref="ProviderManagerService"/>.</summary>
    public ProviderManagerService(IReviLogger<ProviderManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Providers/";
        ProviderProfile[] previous;
        ProviderProfile[] current;

        lock (_writeLock)
        {
            List<ProviderProfile> loaded = [];
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
                _logger.LogError($"Error loading providers: {e.Message}");
            }

            previous = _providers;
            _providers = current = [.. loaded];
        }

        // Replace first, dispose afterwards: callers may still hold the previous profiles, and their decision
        // clients finish in-flight requests before releasing their resources. A profile that is still published
        // (the same instance in the new list) is left alone.
        foreach (ProviderProfile provider in previous)
            if (!current.Any(p => ReferenceEquals(p, provider))) provider.Dispose();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void LoadDirectory(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "Providers") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(path)) return;
        LoadFromFileSystem(path);
    }

    /// <inheritdoc/>
    public ProviderProfile? Get(string name)
        => _providers.FirstOrDefault(p => p.Name == name);

    /// <inheritdoc/>
    public List<ProviderProfile> GetAll()
        => [.._providers];

    /// <inheritdoc/>
    public void Add(ProviderProfile provider)
    {
        lock (_writeLock) _providers = [.. _providers, provider];
    }

    /// <summary>Releases owned decision transports when the service provider shuts down.</summary>
    public void Dispose()
    {
        foreach (ProviderProfile provider in _providers) provider.Dispose();
    }

    /// <summary>Additively loads the <c>.rcfg</c> files under <paramref name="path"/>; existing providers win a name clash.</summary>
    /// <param name="path">The directory to read.</param>
    private void LoadFromFileSystem(string path) => Merge(loaded => ReadFileSystem(path, loaded));

    /// <summary>Publishes the current providers plus whatever <paramref name="read"/> appends, as one swap.</summary>
    /// <param name="read">Appends newly read providers to the working copy it is given.</param>
    private void Merge(Action<List<ProviderProfile>> read)
    {
        lock (_writeLock)
        {
            List<ProviderProfile> merged = [.. _providers];
            read(merged);
            _providers = [.. merged];
        }
    }

    /// <summary>Reads the <c>.rcfg</c> files under <paramref name="path"/> into <paramref name="target"/>.</summary>
    /// <param name="path">The directory to read; a missing one throws <see cref="DirectoryNotFoundException"/>.</param>
    /// <param name="target">The working list new providers are appended to.</param>
    private void ReadFileSystem(string path, List<ProviderProfile> target)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();

        // Per-file try/catch so one malformed provider doesn't abort loading the rest.
        foreach (string file in files)
        {
            try
            {
                Dictionary<string, string> dict = RConfigParser.Read(file);
                // Subfolders are organizational only; the declared name is the registered name.
                ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(dict);

                if (provider?.Name is null)
                    continue;

                CheckAdd(target, provider, embedded: false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"ProviderManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public void LoadAssembly(Assembly assembly) => Merge(loaded => ReadEmbeddedResources(assembly, loaded));

    /// <summary>Reads the provider profiles embedded in <paramref name="assembly"/> into <paramref name="target"/>.</summary>
    /// <param name="assembly">The assembly whose <c>.Providers.</c> resources are read.</param>
    /// <param name="target">The working list new providers are appended to.</param>
    private void ReadEmbeddedResources(Assembly assembly, List<ProviderProfile> target)
    {
        try
        {
            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Providers.") &&
                            n.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            // Per-resource try/catch so one malformed provider doesn't abort loading the rest.
            foreach (string resourceName in resourceNames)
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;

                    using StreamReader reader = new(stream);
                    Dictionary<string, string> dict = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                    // Register under the declared name verbatim (no folder prefix): an embedded resource
                    // name cannot tell a folder dot from a dot inside a file name, and the declared name is
                    // what every lookup uses. See PromptRegistryNameTests.
                    ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(dict);

                    if (provider?.Name is null)
                        continue;

                    CheckAdd(target, provider, embedded: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"ProviderManager: Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading providers from embedded resources: {e.Message}");
        }
    }

    /// <summary>Appends <paramref name="provider"/> to <paramref name="target"/> unless a provider of that name is already there.</summary>
    /// <param name="target">The working list being built.</param>
    /// <param name="provider">The provider just read.</param>
    /// <param name="embedded">Whether it came from an embedded resource (for the log line).</param>
    private void CheckAdd(List<ProviderProfile> target, ProviderProfile provider, bool embedded)
    {
        if (target.Any(p => p.Name == provider.Name))
            return;

        target.Add(provider);
        _logger.LogInfo(embedded
            ? $"Loaded embedded provider \"{provider.Name}\""
            : $"Loaded provider \"{provider.Name}\" from file system");
    }
}
