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

public static class ModelManager
{
    
    private static List<ModelProfile> _models = new();
    
    /// <summary>
    /// Finds the highest-tier model that is enabled, meets or exceeds the specified minimum tier, and optionally checks if the provider supports completions.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <param name="needsPromptCompletion">If true, filters models to include only those whose providers support completions.</param>
    /// <returns>The best matching model if one exists, otherwise null.</returns>
    public static ModelProfile? Find(string? minTier, bool needsPromptCompletion = false)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier, needsPromptCompletion);
    }
    
    /// <summary>
    /// Finds the lowest-tier model that is enabled, meets or exceeds the specified minimum tier, and optionally checks if the provider supports completions.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <param name="needsPromptCompletion">If true, filters models to include only those whose providers support completions.</param>
    /// <returns>The best matching model if one exists, otherwise null.</returns>
    public static ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false)
    {
        // Ensure a default minimum tier is set if none provided
        if (minTier == null)
            minTier = ModelTier.C;

        // Construct the query to find the model that is the least advanced but still meets or exceeds the minimum tier requirement
        return _models
            .Where(model => IsEligibleModel(model, minTier.Value, needsPromptCompletion))
            .MinBy(model => model.Tier);
    }

    /// <summary>
    /// Determines if a model is eligible based on the provided criteria.
    /// </summary>
    /// <param name="model">The model to check.</param>
    /// <param name="minTier">The minimum required tier.</param>
    /// <param name="needsPromptCompletion">Indicates whether the model's provider should support completions.</param>
    /// <returns>True if the model meets all criteria, otherwise false.</returns>
    private static bool IsEligibleModel(ModelProfile model, ModelTier minTier, bool needsPromptCompletion)
    {
        bool isTierSufficient = model.Tier >= minTier;
        bool isCompletionSupported = !needsPromptCompletion || (model.Provider?.SupportsCompletion ?? false);
        return model.Enabled && isTierSufficient && isCompletionSupported;
    }
    
    public static ModelProfile? Get(string name)
    {
        return _models.FirstOrDefault(model => model.Name == name);
    }

    public static void Add(ModelProfile model)
    {
        _models.Add(model);
    }
    
    private static void CheckAdd(ModelProfile newModel)
    {
        var existingModel = _models.FirstOrDefault(p => p.Name == newModel.Name);
        if (existingModel == null)
        {
            _models.Add(newModel);
            Util.Log($"Loading model named \"{newModel.Name}\"");
        }
    }
    
    public static void Load()
    {
        // Clear existing models
        _models.Clear();
        
        // Collect the list of files
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Models/";
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();

        // Load in the files
        Util.Log($"Attempting to load models from {path}");
        foreach (var file in files)
        {
            Dictionary<string, string> modelDictionary = RConfigParser.Read(file);
            string folder = Util.ExtractSubDirectories(path, file).ToLower();
            ModelProfile? model = RConfigParser.ToObject<ModelProfile>(modelDictionary, folder);
            
            if (model?.Name is null)
                continue;
            
            CheckAdd(model);
        }
    }
}