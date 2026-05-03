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

        foreach (string file in files)
        {
            Dictionary<string, string> dict = RConfigParser.Read(file);
            string folder = Util.ExtractSubDirectories(path, file).ToLower();
            ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(dict, folder);

            if (provider?.Name is null)
                continue;

            CheckAdd(provider, embedded: false);
        }
    }

    private void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Providers.") &&
                            n.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            foreach (string resourceName in resourceNames)
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using StreamReader reader = new(stream);
                Dictionary<string, string> dict = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                string folder = Util.ExtractEmbeddedDirectories(".Providers.", resourceName).ToLower();
                ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(dict, folder);

                if (provider?.Name is null)
                    continue;

                CheckAdd(provider, embedded: true);
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
