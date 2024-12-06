// =================================================================================
//   Copyright © 2024 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

using System.Reflection;

namespace Revi;

internal static class ProviderManager
{
    // ==============
    //  Declarations
    // ==============
    
    private static readonly List<ProviderProfile> _providers = new();

    
    // ==================
    //  Provider Loading
    // ==================
    
    #region Provider Loading
    internal static void Load()
    {
        _providers.Clear();

        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Providers/";
        Util.Log($"Attempting to load providers from {path}");
        
        try
        {
            LoadFromFileSystem(path);
        }
        catch (DirectoryNotFoundException e)
        {
            Util.Log($"Directory not found: {e.Message}. Attempting to load from embedded resources.");
            LoadFromEmbeddedResources();
        }
        catch (Exception e)
        {
            Util.Log($"Error loading providers: {e.Message}");
        }
    }

    private static void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();

        foreach (var file in files)
        {
            var providerDictionary = RConfigParser.Read(file);
            string folder = Util.ExtractSubDirectories(path, file).ToLower();
            ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(providerDictionary, folder);

            if (provider?.Name is null)
                continue;

            CheckAdd(provider);
        }
    }

    private static void LoadFromEmbeddedResources()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Providers.") && 
                               name.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using var reader = new StreamReader(stream);
                var providerDictionary = RConfigParser.Read(reader.ReadToEnd());
                const string folder = "embedded";
                ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(providerDictionary, folder);

                if (provider?.Name is null)
                    continue;

                CheckAdd(provider);
            }
        }
        catch (Exception e)
        {
            Util.Log($"Error loading from embedded resources: {e.Message}");
        }
    }
    #endregion
    
    
    // ======================
    //  Supporting Functions
    // ======================
    
    private static void CheckAdd(ProviderProfile newProvider)
    {
        var existingProvider = _providers.FirstOrDefault(p => p.Name == newProvider.Name);
        if (existingProvider == null)
        {
            _providers.Add(newProvider);
            Util.Log($"Loading provider named \"{newProvider.Name}\"");
        }
    }

    
    // ===============
    //  Accessibility
    // ===============
    
    internal static ProviderProfile? Get(string name)
    {
        return _providers.FirstOrDefault(provider => provider.Name == name);
    }

    internal static void Add(ProviderProfile model)
    {
        _providers.Add(model);
    }
}