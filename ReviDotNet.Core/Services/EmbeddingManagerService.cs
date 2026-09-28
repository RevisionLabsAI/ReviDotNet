// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Service implementation of <see cref="IEmbeddingManager"/>. Holds loaded embedding profiles as instance state.
/// Reads never lock and are safe during a reload: every write builds a complete replacement list and publishes it
/// with one reference swap, so a reader sees either the old list or the new one, never a partial or empty one.
/// </summary>
public sealed class EmbeddingManagerService : IEmbeddingManager
{
    /// <summary>Serialises writers so concurrent loads and adds cannot lose each other's entries. Readers never take it.</summary>
    private readonly object _writeLock = new();
    /// <summary>The published embedding models. Never mutated once assigned; writers replace the whole array under <see cref="_writeLock"/>.</summary>
    private volatile EmbeddingProfile[] _models = [];
    private readonly IProviderManager _providers;
    private readonly IReviLogger<EmbeddingManagerService> _logger;

    /// <summary>Initializes a new <see cref="EmbeddingManagerService"/>.</summary>
    /// <param name="providers">The provider registry used to resolve provider references on each model after deserialization.</param>
    /// <param name="logger">The logger instance.</param>
    public EmbeddingManagerService(IProviderManager providers, IReviLogger<EmbeddingManagerService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Models/Embedding/";

        lock (_writeLock)
        {
            List<EmbeddingProfile> loaded = [];
            try
            {
                ReadFileSystem(path, loaded);
            }
            catch (DirectoryNotFoundException)
            {
                ReadEmbeddedResources(assembly, loaded);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error loading embedding models: {e.Message}");
            }

            _models = [.. loaded];
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void LoadDirectory(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "Models", "Embedding") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(path)) return;
        LoadFromFileSystem(path);
    }

    /// <inheritdoc/>
    public EmbeddingProfile? Get(string name)
        => _models.FirstOrDefault(m => m.Name == name);

    /// <inheritdoc/>
    public IReadOnlyList<EmbeddingProfile> GetAll()
        => Array.AsReadOnly(_models);

    /// <inheritdoc/>
    public List<EmbeddingProfile> GetAllEnabled()
        => _models.Where(m => m.Enabled).ToList();

    /// <inheritdoc/>
    public void Add(EmbeddingProfile embeddingModel)
    {
        lock (_writeLock) _models = [.. _models, embeddingModel];
    }

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

    /// <summary>Additively loads the <c>.rcfg</c> files under <paramref name="path"/>; existing models win a name clash.</summary>
    /// <param name="path">The directory to read.</param>
    private void LoadFromFileSystem(string path) => Merge(loaded => ReadFileSystem(path, loaded));

    /// <summary>Publishes the current embedding models plus whatever <paramref name="read"/> appends, as one swap.</summary>
    /// <param name="read">Appends newly read models to the working copy it is given.</param>
    private void Merge(Action<List<EmbeddingProfile>> read)
    {
        lock (_writeLock)
        {
            List<EmbeddingProfile> merged = [.. _models];
            read(merged);
            _models = [.. merged];
        }
    }

    /// <summary>Reads the <c>.rcfg</c> files under <paramref name="path"/> into <paramref name="target"/>.</summary>
    /// <param name="path">The directory to read; a missing one throws <see cref="DirectoryNotFoundException"/>.</param>
    /// <param name="target">The working list new models are appended to.</param>
    private void ReadFileSystem(string path, List<EmbeddingProfile> target)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();

        // Per-file try/catch so one malformed embedding model doesn't abort loading the rest.
        foreach (string file in files)
        {
            try
            {
                Dictionary<string, string> dict = RConfigParser.Read(file);
                // Subfolders are organizational only; the declared name is the registered name.
                EmbeddingProfile? model = RConfigParser.ToObject<EmbeddingProfile>(dict);

                if (model?.Name is null)
                    continue;

                model.ResolveProvider(_providers);
                CheckAdd(target, model, embedded: false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"EmbeddingManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public void LoadAssembly(Assembly assembly) => Merge(loaded => ReadEmbeddedResources(assembly, loaded));

    /// <summary>Reads the embedding profiles embedded in <paramref name="assembly"/> into <paramref name="target"/>.</summary>
    /// <param name="assembly">The assembly whose <c>.Models.Embedding.</c> resources are read.</param>
    /// <param name="target">The working list new models are appended to.</param>
    private void ReadEmbeddedResources(Assembly assembly, List<EmbeddingProfile> target)
    {
        try
        {
            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Models.Embedding.") &&
                            n.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            // Per-resource try/catch so one malformed embedding model doesn't abort loading the rest.
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
                    EmbeddingProfile? model = RConfigParser.ToObject<EmbeddingProfile>(dict);

                    if (model?.Name is null)
                        continue;

                    model.ResolveProvider(_providers);
                    CheckAdd(target, model, embedded: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"EmbeddingManager: Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading embedding models from embedded resources: {e.Message}");
        }
    }

    /// <summary>Appends <paramref name="model"/> to <paramref name="target"/> unless a model of that name is already there.</summary>
    /// <param name="target">The working list being built.</param>
    /// <param name="model">The embedding model just read.</param>
    /// <param name="embedded">Whether it came from an embedded resource (for the log line).</param>
    private void CheckAdd(List<EmbeddingProfile> target, EmbeddingProfile model, bool embedded)
    {
        if (target.Any(m => m.Name == model.Name))
            return;

        target.Add(model);
        _logger.LogInfo(embedded
            ? $"Loaded embedded embedding model \"{model.Name}\""
            : $"Loaded embedding model \"{model.Name}\" from file system");
    }
}
