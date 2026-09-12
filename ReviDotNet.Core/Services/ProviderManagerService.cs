// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>Service implementation of <see cref="IProviderManager"/>. Holds loaded provider profiles as instance state.</summary>
public sealed class ProviderManagerService : IProviderManager
{
    private readonly List<ProviderProfile> _providers = [];
    private readonly IReviLogger<ProviderManagerService> _logger;

    /// <summary>Initializes a new <see cref="ProviderManagerService"/>.</summary>
    public ProviderManagerService(IReviLogger<ProviderManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        _providers.Clear();

        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Providers/";

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
            _logger.LogError($"Error loading providers: {e.Message}");
        }

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
        => _providers.Add(provider);

    private void LoadFromFileSystem(string path)
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

                CheckAdd(provider, embedded: false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"ProviderManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    private void LoadFromEmbeddedResources(Assembly assembly)
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

                    CheckAdd(provider, embedded: true);
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

    private void CheckAdd(ProviderProfile provider, bool embedded)
    {
        if (_providers.Any(p => p.Name == provider.Name))
            return;

        _providers.Add(provider);
        _logger.LogInfo(embedded
            ? $"Loaded embedded provider \"{provider.Name}\""
            : $"Loaded provider \"{provider.Name}\" from file system");
    }
}
