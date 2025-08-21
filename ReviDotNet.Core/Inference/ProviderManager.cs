// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
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

public static class ProviderManager
{
    // ==============
    //  Declarations
    // ==============

    /// <summary>
    /// A private, static, readonly list that stores instances of <see cref="ProviderProfile"/>.
    /// It is used to manage the collection of provider profiles within the <see cref="ProviderManager"/>.
    /// </summary>
    private static readonly List<ProviderProfile> _providers = new();

    
    // ==================
    //  Provider Loading
    // ==================
    
    #region Provider Loading

    /// <summary>
    /// Loads provider profiles from the specified directory path.
    /// If the directory is not found, it attempts to load providers from embedded resources.
    /// Logs operations and errors encountered during the loading process.
    /// </summary>
    /// <remarks>
    /// This method will clear the existing provider profiles before loading new ones.
    /// It handles exceptions related to directory access and general loading errors.
    /// </remarks>
    public static void Load(Assembly assembly = null)
    {
        _providers.Clear();

        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Providers/";
        //Util.Log($"Attempting to load providers from {path}");
        
        try
        {
            LoadFromFileSystem(path);
        }
        catch (DirectoryNotFoundException e)
        {
            //Util.Log($"Directory not found: {e.Message}. Attempting to load from embedded resources.");
            LoadFromEmbeddedResources(assembly);
        }
        catch (Exception e)
        {
            Util.Log($"Error loading providers: {e.Message}");
        }
    }

    /// <summary>
    /// Loads provider profiles from the file system at the specified path.
    /// Reads configuration files and parses them into provider profiles.
    /// Verifies and adds valid provider profiles to the collection.
    /// </summary>
    /// <param name="path">The directory path from which provider profiles are to be loaded.</param>
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

            CheckAdd(provider, false);
        }
    }

    /// <summary>
    /// Loads provider profiles from embedded resources when the directory path is not available.
    /// Parses and converts embedded provider files into provider profiles.
    /// </summary>
    /// <remarks>
    /// This method handles exceptions related to resource access and parsing errors.
    /// Successfully loaded provider profiles are added to the existing collection.
    /// </remarks>
    private static void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            if (assembly is null)
                throw new Exception("Assembly cannot be null.");
            
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Providers.") && 
                               name.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) 
                {
                    Util.Log($"Stream not found for resource: {resourceName}");
                    continue;
                }

                using var reader = new StreamReader(stream);
                var providerDictionary = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                string folder = Util.ExtractEmbeddedDirectories(".Providers.", resourceName).ToLower();
                ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(providerDictionary, folder);

                if (provider?.Name is null)
                    continue;

                CheckAdd(provider, true);
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

    /// <summary>
    /// Adds a new provider profile to the internal collection if it does not already exist.
    /// Logs the addition if the provider is successfully added.
    /// </summary>
    /// <param name="newProvider">The provider profile to be added to the collection.</param>
    private static void CheckAdd(ProviderProfile newProvider, bool embedded)
    {
        var existingProvider = _providers.FirstOrDefault(p => p.Name == newProvider.Name);
        if (existingProvider == null)
        {
            _providers.Add(newProvider);
            if (embedded)
                Util.Log($"Loaded embedded provider \"{newProvider.Name}\"");
            else
                Util.Log($"Loaded provider \"{newProvider.Name}\" from file system");
        }
    }

    
    // ===============
    //  Accessibility
    // ===============

    /// <summary>
    /// Retrieves a provider profile by its name from the list of loaded providers.
    /// </summary>
    /// <param name="name">The name of the provider profile to retrieve.</param>
    /// <returns>
    /// The <see cref="ProviderProfile"/> associated with the specified name, or <c>null</c> if no profile matches the provided name.
    /// </returns>
    public static ProviderProfile? Get(string name)
    {
        return _providers.FirstOrDefault(provider => provider.Name == name);
    }

    /// <summary>
    /// Adds a new provider profile to the collection of provider profiles.
    /// </summary>
    /// <param name="model">The provider profile to be added.</param>
    public static void Add(ProviderProfile model)
    {
        _providers.Add(model);
    }
}