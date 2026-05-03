// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>Service implementation of <see cref="IModelManager"/>. Holds loaded model profiles as instance state.</summary>
public sealed class ModelManagerService : IModelManager
{
    private readonly List<ModelProfile> _models = [];
    private readonly IReviLogger<ModelManagerService> _logger;

    /// <summary>Initializes a new <see cref="ModelManagerService"/>.</summary>
    public ModelManagerService(IReviLogger<ModelManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        _models.Clear();

        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Models/Inference/";

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
            _logger.LogError($"Error loading models: {e.Message}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ModelProfile? Get(string name)
        => _models.FirstOrDefault(m => m.Name == name);

    /// <inheritdoc/>
    public List<ModelProfile> GetAll()
        => [.._models];

    /// <inheritdoc/>
    public void Add(ModelProfile model)
        => _models.Add(model);

    /// <inheritdoc/>
    public ModelProfile? Find(string? minTier, bool needsPromptCompletion = false)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier, needsPromptCompletion);
    }

    /// <inheritdoc/>
    public ModelProfile? Find(string? minTier, bool needsPromptCompletion, List<string>? blockedModels)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier, needsPromptCompletion, blockedModels);
    }

    /// <inheritdoc/>
    public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false)
    {
        ModelTier tier = minTier ?? ModelTier.C;
        return _models
            .Where(m => IsEligible(m, tier, needsPromptCompletion))
            .MinBy(m => m.Tier);
    }

    /// <inheritdoc/>
    public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion, List<string>? blockedModels)
    {
        ModelTier tier = minTier ?? ModelTier.C;
        return _models
            .Where(m => IsEligible(m, tier, needsPromptCompletion))
            .Where(m => blockedModels == null || !blockedModels.Contains(m.Name))
            .MinBy(m => m.Tier);
    }

    private static bool IsEligible(ModelProfile model, ModelTier minTier, bool needsPromptCompletion)
        => model.Enabled &&
           model.Tier >= minTier &&
           (!needsPromptCompletion || (model.Provider?.SupportsCompletion ?? false));

    private void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();

        foreach (string file in files)
        {
            Dictionary<string, string> dict = RConfigParser.Read(file);
            string folder = Util.ExtractSubDirectories(path, file).ToLower();
            ModelProfile? model = RConfigParser.ToObject<ModelProfile>(dict, folder);

            if (model?.Name is null)
                continue;

            CheckAdd(model, embedded: false);
        }
    }

    private void LoadFromEmbeddedResources(Assembly assembly)
    {
        try
        {
            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Models.Inference.") &&
                            n.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            foreach (string resourceName in resourceNames)
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using StreamReader reader = new(stream);
                Dictionary<string, string> dict = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                string folder = Util.ExtractEmbeddedDirectories(".Models.Inference.", resourceName).ToLower();
                ModelProfile? model = RConfigParser.ToObject<ModelProfile>(dict, folder);

                if (model?.Name is null)
                    continue;

                CheckAdd(model, embedded: true);
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading models from embedded resources: {e.Message}");
        }
    }

    private void CheckAdd(ModelProfile model, bool embedded)
    {
        if (_models.Any(m => m.Name == model.Name))
            return;

        _models.Add(model);
        _logger.LogInfo(embedded
            ? $"Loaded embedded model \"{model.Name}\""
            : $"Loaded model \"{model.Name}\" from file system");
    }
}
