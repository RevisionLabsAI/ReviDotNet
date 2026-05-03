// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>Service implementation of <see cref="IEmbeddingManager"/>. Holds loaded embedding profiles as instance state.</summary>
public sealed class EmbeddingManagerService : IEmbeddingManager
{
    private readonly List<EmbeddingProfile> _models = [];
    private readonly IReviLogger<EmbeddingManagerService> _logger;

    /// <summary>Initializes a new <see cref="EmbeddingManagerService"/>.</summary>
    public EmbeddingManagerService(IReviLogger<EmbeddingManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        _models.Clear();

        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Models/Embedding/";

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
            _logger.LogError($"Error loading embedding models: {e.Message}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public EmbeddingProfile? Get(string name)
        => _models.FirstOrDefault(m => m.Name == name);

    /// <inheritdoc/>
    public IReadOnlyList<EmbeddingProfile> GetAll()
        => _models.AsReadOnly();

    /// <inheritdoc/>
    public List<EmbeddingProfile> GetAllEnabled()
        => _models.Where(m => m.Enabled).ToList();

    /// <inheritdoc/>
    public void Add(EmbeddingProfile embeddingModel)
        => _models.Add(embeddingModel);

    /// <inheritdoc/>
    public EmbeddingProfile? Find(string? minTier)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier);
    }

    /// <inheritdoc/>
    public EmbeddingProfile? Find(string? minTier, List<string>? blockedModels)
    {
        Enum.TryParse(minTier ?? "", out ModelTier foundTier);
        return Find(foundTier, blockedModels);
    }

    /// <inheritdoc/>
    public EmbeddingProfile? Find(ModelTier? minTier)
    {
        ModelTier tier = minTier ?? ModelTier.C;
        return _models
            .Where(m => m.Enabled && m.Tier >= tier)
            .MinBy(m => m.Tier);
    }

    /// <inheritdoc/>
    public EmbeddingProfile? Find(ModelTier? minTier, List<string>? blockedModels)
    {
        ModelTier tier = minTier ?? ModelTier.C;
        return _models
            .Where(m => m.Enabled && m.Tier >= tier)
            .Where(m => blockedModels == null || !blockedModels.Contains(m.Name))
            .MinBy(m => m.Tier);
    }

    private void LoadFromFileSystem(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();

        foreach (string file in files)
        {
            Dictionary<string, string> dict = RConfigParser.Read(file);
            string folder = Util.ExtractSubDirectories(path, file).ToLower();
            EmbeddingProfile? model = RConfigParser.ToObject<EmbeddingProfile>(dict, folder);

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
                .Where(n => n.Contains(".Models.Embedding.") &&
                            n.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            foreach (string resourceName in resourceNames)
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using StreamReader reader = new(stream);
                Dictionary<string, string> dict = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                string folder = Util.ExtractEmbeddedDirectories(".Models.Embedding.", resourceName).ToLower();
                EmbeddingProfile? model = RConfigParser.ToObject<EmbeddingProfile>(dict, folder);

                if (model?.Name is null)
                    continue;

                CheckAdd(model, embedded: true);
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading embedding models from embedded resources: {e.Message}");
        }
    }

    private void CheckAdd(EmbeddingProfile model, bool embedded)
    {
        if (_models.Any(m => m.Name == model.Name))
            return;

        _models.Add(model);
        _logger.LogInfo(embedded
            ? $"Loaded embedded embedding model \"{model.Name}\""
            : $"Loaded embedding model \"{model.Name}\" from file system");
    }
}
