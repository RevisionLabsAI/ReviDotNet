// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

public static class ModelManager
{
    // ==============
    //  Declarations
    // ==============

    /// <summary>
    /// A private static list that holds instances of ModelProfile.
    /// It is used to manage and store different model profiles within the application.
    /// The list is utilized for operations such as loading models from a file system,
    /// adding new models, and retrieving specific models by their names.
    /// </summary>
    private static List<ModelProfile> _models = new();
    
    
    // ==================
    //  Model Loading
    // ==================

    #region Model Loading

    /// <summary>
    /// Clears existing models and loads model profiles from the default path.
    /// If the specified directory is not found, it attempts to load models from embedded resources.
    /// Logs the process of loading models and handles potential exceptions.
    /// </summary>
    public static void Load(Assembly assembly = null)
    {
        // Clear existing models
        _models.Clear();

        // Updated path to load from Inference subdirectory
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Models/Inference/";
        //Util.Log($"Attempting to load models from {path}");
        
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
            Util.Log($"Error loading models: {e.Message}");
        }
    }

    /// <summary>
    /// Loads model profiles from the filesystem.
    /// </summary>
    /// <param name="path">The directory path where model profiles are located.</param>
    private static void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();
        
        // Load and check each file
        foreach (var file in files)
        {
            Dictionary<string, string> modelDictionary = RConfigParser.Read(file);
            string folder = Util.ExtractSubDirectories(path, file).ToLower();
            ModelProfile? model = RConfigParser.ToObject<ModelProfile>(modelDictionary, folder);

            if (model?.Name is null)
                continue;

            CheckAdd(model, false);
        }
    }

    /// <summary>
    /// Loads model profiles from embedded resources present within the assembly,
    /// and attempts to add them to the existing collection of models. It specifically
    /// looks for resources with names containing ".Models.Inference." and ending with ".rcfg".
    /// </summary>
    private static void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            if (assembly is null)
                throw new Exception("Assembly cannot be null.");
 
            // Updated to look specifically for inference models
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Models.Inference.") && 
                               name.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using var reader = new StreamReader(stream);
                var modelDictionary = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                string folder = Util.ExtractEmbeddedDirectories(".Models.Inference.", resourceName).ToLower();
                ModelProfile? model = RConfigParser.ToObject<ModelProfile>(modelDictionary, folder);

                if (model?.Name is null)
                    continue;

                CheckAdd(model, true);
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
    
    #region Supporting Functions
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

    /// <summary>
    /// Attempts to add a new model profile to the list of models.
    /// If a model with the same name already exists, the new model is not added.
    /// Logs the action of loading a new model.
    /// </summary>
    /// <param name="newModel">The new ModelProfile to consider adding to the existing models.</param>
    private static void CheckAdd(ModelProfile newModel, bool embedded)
    {
        var existingModel = _models.FirstOrDefault(p => p.Name == newModel.Name);
        if (existingModel == null)
        {
            _models.Add(newModel);
            if (embedded)
                Util.Log($"Loaded embedded model \"{newModel.Name}\"");
            else
                Util.Log($"Loaded model \"{newModel.Name}\" from file system");
        }
    }
    /// <summary>
    /// Returns all loaded model profiles.
    /// </summary>
    /// <returns>A list of all model profiles.</returns>
    public static List<ModelProfile> GetAll()
    {
        return _models.ToList();
    }
    #endregion
    
    
    // ===============
    //  Accessibility
    // ===============
    
    #region Accessibility
    /// <summary>
    /// Retrieves a model profile with the specified name from the collection of loaded models.
    /// </summary>
    /// <param name="name">The name of the model profile to retrieve.</param>
    /// <returns>
    /// The <see cref="ModelProfile"/> object matching the specified name, or null if no such model is found.
    /// </returns>
    public static ModelProfile? Get(string name)
    {
        return _models.FirstOrDefault(model => model.Name == name);
    }

    /// <summary>
    /// Adds a new model profile to the list of currently loaded models.
    /// </summary>
    /// <param name="model">The model profile to be added.</param>
    public static void Add(ModelProfile model)
    {
        _models.Add(model);
    }
    
    /// <summary>
    /// Finds the lowest-tier model that is enabled, meets or exceeds the specified minimum tier, and optionally checks if the provider supports completions.
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
    /// Finds the lowest-tier model that is enabled, meets or exceeds the specified minimum tier, 
    /// optionally checks if the provider supports completions, and excludes models in the blocked list.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <param name="needsPromptCompletion">If true, filters models to include only those whose providers support completions.</param>
    /// <param name="blockedModels">List of model names to exclude from selection.</param>
    /// <returns>The best matching model if one exists, otherwise null.</returns>
    public static ModelProfile? Find(string? minTier, bool needsPromptCompletion, List<string>? blockedModels)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier, needsPromptCompletion, blockedModels);
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

        // Construct the query to find the model that is the best available but still meets or exceeds the minimum tier requirement
        return _models
            .Where(model => IsEligibleModel(model, minTier.Value, needsPromptCompletion))
            .MinBy(model => model.Tier);
    }

    /// <summary>
    /// Finds the lowest-tier model that is enabled, meets or exceeds the specified minimum tier, 
    /// optionally checks if the provider supports completions, and excludes models in the blocked list.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <param name="needsPromptCompletion">If true, filters models to include only those whose providers support completions.</param>
    /// <param name="blockedModels">List of model names to exclude from selection.</param>
    /// <returns>The best matching model if one exists, otherwise null.</returns>
    public static ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion, List<string>? blockedModels)
    {
        // Ensure a default minimum tier is set if none provided
        if (minTier == null)
            minTier = ModelTier.C;

        // Construct the query to find the model that is the best available but still meets or exceeds the minimum tier requirement
        // and is not in the blocked list
        return _models
            .Where(model => IsEligibleModel(model, minTier.Value, needsPromptCompletion))
            .Where(model => blockedModels == null || !blockedModels.Contains(model.Name))
            .MinBy(model => model.Tier);
    }
    #endregion
}