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

using System.Runtime.InteropServices.ObjectiveC;
using Newtonsoft.Json;
using Revi;

namespace Revi;

public static class ProviderManager
{
    private static List<ProviderProfile> _providers = new();

    public static ProviderProfile? Get(string name)
    {
        return _providers.FirstOrDefault(provider => provider.Name == name);
    }

    public static void Add(ProviderProfile model)
    {
        _providers.Add(model);
    }
    
    private static void CheckAdd(ProviderProfile newProvider)
    {
        var existingProvider = _providers.FirstOrDefault(p => p.Name == newProvider.Name);
        if (existingProvider == null)
        {
            _providers.Add(newProvider);
            Util.Log($"Loading provider named \"{newProvider.Name}\"");
        }
    }
    
    public static void Load()
    {
        // Clear existing providers
        _providers.Clear();
        
        // Collect the list of files
        try
        {
            string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Providers/";
            Util.Log($"Attempting to load providers from {path}");
            List<string> files = Directory
                .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
                .ToList();

            // Load in the files

            foreach (var file in files)
            {
                Dictionary<string, string> providerDictionary = RConfigParser.Read(file);
                string folder = Util.ExtractSubDirectories(path, file).ToLower();
                ProviderProfile? provider = RConfigParser.ToObject<ProviderProfile>(providerDictionary, folder);

                if (provider?.Name is null)
                    continue;

                CheckAdd(provider);
            }
        }
        catch (DirectoryNotFoundException e)
        {
            Util.Log($"Directory not found: {e.Message}");
        }
        catch (Exception e)
        {
            Util.Log($"Error loading providers: {e.Message}");
        }
    }

}