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

/// <summary>
/// Manages the loading, storage, and retrieval of embedding model profiles.
/// Provides static methods to access configured embedding models from the RConfigs/Models/Embedding/ directory.
/// </summary>
public static class EmbeddingManager
{
    // ==============
    //  Declarations
    // ==============

    /// <summary>
    /// A private static list that holds instances of EmbeddingProfile.
    /// It is used to manage and store different embedding model profiles within the application.
    /// The list is utilized for operations such as loading models from a file system,
    /// adding new models, and retrieving specific models by their names.
    /// </summary>
    private static List<EmbeddingProfile> _embeddingModels = new();
    
    
    // ==================
    //  Model Loading
    // ==================

    #region Model Loading

    /// <summary>
    /// Clears existing embedding models and loads embedding model profiles from the default path.
    /// If the specified directory is not found, it attempts to load models from embedded resources.
    /// Logs the process of loading models and handles potential exceptions.
    /// </summary>
    /// <param name="assembly">Optional assembly to load embedded resources from. If null, only filesystem loading is attempted.</param>
    public static void Load(Assembly? assembly = null)
    {
        // Clear existing embedding models
        _embeddingModels.Clear();

        // Path specifically for embedding models
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Models/Embedding/";
        
        try
        {
            LoadFromFileSystem(path);
        }
        catch (DirectoryNotFoundException e)
        {
            // If filesystem directory not found, try embedded resources
            if (assembly != null)
            {
                LoadFromEmbeddedResources(assembly);
            }
            else
            {
                Util.Log($"Embedding models directory not found: {e.Message}");
            }
        }
        catch (Exception e)
        {
            Util.Log($"Error loading embedding models: {e.Message}");
        }
    }

    /// <summary>
    /// Loads embedding model profiles from the filesystem.
    /// Searches recursively for all .rcfg files in the specified directory.
    /// </summary>
    /// <param name="path">The directory path where embedding model profiles are located.</param>
    private static void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();
        
        // Load and check each file
        foreach (var file in files)
        {
            // Read the configuration file into a dictionary
            Dictionary<string, string> embeddingDictionary = RConfigParser.Read(file);
            
            // Extract subdirectory information for context
            string folder = Util.ExtractSubDirectories(path, file).ToLower();
            
            // Convert dictionary to EmbeddingProfile object
            EmbeddingProfile? embeddingModel = RConfigParser.ToObject<EmbeddingProfile>(embeddingDictionary, folder);

            // Skip if model name is null
            if (embeddingModel?.Name is null)
                continue;

            // Add to the collection
            CheckAdd(embeddingModel, false);
        }
    }

    /// <summary>
    /// Loads embedding model profiles from embedded resources present within the assembly,
    /// and attempts to add them to the existing collection of embedding models. 
    /// It specifically looks for resources with names containing ".Models.Embedding." and ending with ".rcfg".
    /// </summary>
    /// <param name="assembly">The assembly containing the embedded resources.</param>
    private static void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            if (assembly is null)
                throw new Exception("Assembly cannot be null.");
 
            // Find embedded resources for embedding models
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Models.Embedding.") && 
                               name.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using var reader = new StreamReader(stream);
                var embeddingDictionary = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                string folder = Util.ExtractEmbeddedDirectories(".Models.Embedding.", resourceName).ToLower();
                EmbeddingProfile? embeddingModel = RConfigParser.ToObject<EmbeddingProfile>(embeddingDictionary, folder);

                if (embeddingModel?.Name is null)
                    continue;

                CheckAdd(embeddingModel, true);
            }
        }
        catch (Exception e)
        {
            Util.Log($"Error loading embedding models from embedded resources: {e.Message}");
        }
    }
    #endregion
    

    // ======================
    //  Supporting Functions
    // ======================
    
    #region Supporting Functions
    
    /// <summary>
    /// Determines if an embedding model is eligible based on the provided criteria.
    /// </summary>
    /// <param name="embeddingModel">The embedding model to check.</param>
    /// <param name="minTier">The minimum required tier.</param>
    /// <returns>True if the model meets all criteria, otherwise false.</returns>
    private static bool IsEligibleModel(EmbeddingProfile embeddingModel, ModelTier minTier)
    {
        bool isTierSufficient = embeddingModel.Tier >= minTier;
        return embeddingModel.Enabled && isTierSufficient;
    }

    /// <summary>
    /// Attempts to add a new embedding model profile to the list of models.
    /// If an embedding model with the same name already exists, the new model is not added.
    /// Logs the action of loading a new embedding model.
    /// </summary>
    /// <param name="newEmbeddingModel">The new EmbeddingProfile to consider adding to the existing models.</param>
    /// <param name="embedded">Indicates whether the model is loaded from embedded resources.</param>
    private static void CheckAdd(EmbeddingProfile newEmbeddingModel, bool embedded)
    {
        var existingModel = _embeddingModels.FirstOrDefault(p => p.Name == newEmbeddingModel.Name);
        if (existingModel == null)
        {
            _embeddingModels.Add(newEmbeddingModel);
            if (embedded)
                Util.Log($"Loaded embedded embedding model \"{newEmbeddingModel.Name}\"");
            else
                Util.Log($"Loaded embedding model \"{newEmbeddingModel.Name}\" from file system");
        }
    }
    #endregion
    
    
    // ===============
    //  Accessibility
    // ===============
    
    #region Accessibility
    
    /// <summary>
    /// Retrieves an embedding model profile with the specified name from the collection of loaded models.
    /// </summary>
    /// <param name="name">The name of the embedding model profile to retrieve.</param>
    /// <returns>
    /// The <see cref="EmbeddingProfile"/> object matching the specified name, or null if no such model is found.
    /// </returns>
    public static EmbeddingProfile? Get(string name)
    {
        return _embeddingModels.FirstOrDefault(model => model.Name == name);
    }

    /// <summary>
    /// Adds a new embedding model profile to the list of currently loaded models.
    /// </summary>
    /// <param name="embeddingModel">The embedding model profile to be added.</param>
    public static void Add(EmbeddingProfile embeddingModel)
    {
        _embeddingModels.Add(embeddingModel);
    }
    
    /// <summary>
    /// Finds the lowest-tier embedding model that is enabled and meets or exceeds the specified minimum tier.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <returns>The best matching embedding model if one exists, otherwise null.</returns>
    public static EmbeddingProfile? Find(string? minTier)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier);
    }

    /// <summary>
    /// Finds the lowest-tier embedding model that is enabled, meets or exceeds the specified minimum tier,
    /// and excludes models in the blocked list.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <param name="blockedModels">List of model names to exclude from selection.</param>
    /// <returns>The best matching embedding model if one exists, otherwise null.</returns>
    public static EmbeddingProfile? Find(string? minTier, List<string>? blockedModels)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier, blockedModels);
    }
    
    /// <summary>
    /// Finds the lowest-tier embedding model that is enabled and meets or exceeds the specified minimum tier.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <returns>The best matching embedding model if one exists, otherwise null.</returns>
    public static EmbeddingProfile? Find(ModelTier? minTier)
    {
        // Ensure a default minimum tier is set if none provided
        if (minTier == null)
            minTier = ModelTier.C;

        // Construct the query to find the model that is the best available but still meets or exceeds the minimum tier requirement
        return _embeddingModels
            .Where(model => IsEligibleModel(model, minTier.Value))
            .MinBy(model => model.Tier);
    }

    /// <summary>
    /// Finds the lowest-tier embedding model that is enabled, meets or exceeds the specified minimum tier,
    /// and excludes models in the blocked list.
    /// </summary>
    /// <param name="minTier">The minimum tier required. If null, defaults to ModelTier.C.</param>
    /// <param name="blockedModels">List of model names to exclude from selection.</param>
    /// <returns>The best matching embedding model if one exists, otherwise null.</returns>
    public static EmbeddingProfile? Find(ModelTier? minTier, List<string>? blockedModels)
    {
        // Ensure a default minimum tier is set if none provided
        if (minTier == null)
            minTier = ModelTier.C;

        // Construct the query to find the model that is the best available but still meets or exceeds the minimum tier requirement
        // and is not in the blocked list
        return _embeddingModels
            .Where(model => IsEligibleModel(model, minTier.Value))
            .Where(model => blockedModels == null || !blockedModels.Contains(model.Name))
            .MinBy(model => model.Tier);
    }
    
    /// <summary>
    /// Gets all loaded embedding model profiles.
    /// </summary>
    /// <returns>A read-only list of all embedding models.</returns>
    public static IReadOnlyList<EmbeddingProfile> GetAll()
    {
        return _embeddingModels.AsReadOnly();
    }
    
    /// <summary>
    /// Gets all enabled embedding model profiles.
    /// </summary>
    /// <returns>A list of all enabled embedding models.</returns>
    public static List<EmbeddingProfile> GetAllEnabled()
    {
        return _embeddingModels.Where(m => m.Enabled).ToList();
    }
    #endregion
}
