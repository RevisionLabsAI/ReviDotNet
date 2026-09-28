// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Service implementation of <see cref="IModelManager"/>. Holds loaded model profiles as instance state.
/// Reads never lock and are safe during a reload: every write builds a complete replacement list and publishes it
/// with one reference swap, so a reader sees either the old list or the new one, never a partial or empty one.
/// </summary>
public sealed class ModelManagerService : IModelManager
{
    /// <summary>Serialises writers so concurrent loads and adds cannot lose each other's entries. Readers never take it.</summary>
    private readonly object _writeLock = new();
    /// <summary>The published models. Never mutated once assigned; writers replace the whole array under <see cref="_writeLock"/>.</summary>
    private volatile ModelProfile[] _models = [];
    private readonly IProviderManager _providers;
    private readonly IReviLogger<ModelManagerService> _logger;

    /// <summary>Initializes a new <see cref="ModelManagerService"/>.</summary>
    /// <param name="providers">The provider registry used to resolve provider references on each model after deserialization.</param>
    /// <param name="logger">The logger instance.</param>
    public ModelManagerService(IProviderManager providers, IReviLogger<ModelManagerService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Models/Inference/";

        lock (_writeLock)
        {
            List<ModelProfile> loaded = [];
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
                _logger.LogError($"Error loading models: {e.Message}");
            }

            _models = [.. loaded];
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void LoadDirectory(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "Models", "Inference") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(path)) return;
        LoadFromFileSystem(path);
    }

    /// <inheritdoc/>
    public ModelProfile? Get(string name)
        => _models.FirstOrDefault(m => m.Name == name);

    /// <inheritdoc/>
    public List<ModelProfile> GetAll()
        => [.._models];

    /// <inheritdoc/>
    public void Add(ModelProfile model)
    {
        lock (_writeLock) _models = [.. _models, model];
    }

    /// <inheritdoc/>
    public ModelProfile? Find(string? minTier, bool needsPromptCompletion = false)
    {
        // Case-insensitive so a lowercase a/b/c resolves correctly instead of silently defaulting to C.
        Enum.TryParse(minTier ?? "", ignoreCase: true, out ModelTier foundTier);
        return Find(foundTier, needsPromptCompletion);
    }

    /// <inheritdoc/>
    public ModelProfile? Find(string? minTier, bool needsPromptCompletion, List<string>? blockedModels)
    {
        // Case-insensitive so a lowercase a/b/c resolves correctly instead of silently defaulting to C.
        Enum.TryParse(minTier ?? "", ignoreCase: true, out ModelTier foundTier);
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
           model.AllowAutomaticSelection &&
           model.Tier >= minTier &&
           // Honor a model-level supports-prompt-completion override before the provider's.
           (!needsPromptCompletion || model.EffectiveSupportsPromptCompletion);

    /// <summary>Additively loads the <c>.rcfg</c> files under <paramref name="path"/>; existing models win a name clash.</summary>
    /// <param name="path">The directory to read.</param>
    private void LoadFromFileSystem(string path) => Merge(loaded => ReadFileSystem(path, loaded));

    /// <summary>Publishes the current models plus whatever <paramref name="read"/> appends, as one swap.</summary>
    /// <param name="read">Appends newly read models to the working copy it is given.</param>
    private void Merge(Action<List<ModelProfile>> read)
    {
        lock (_writeLock)
        {
            List<ModelProfile> merged = [.. _models];
            read(merged);
            _models = [.. merged];
        }
    }

    /// <summary>Reads the <c>.rcfg</c> files under <paramref name="path"/> into <paramref name="target"/>.</summary>
    /// <param name="path">The directory to read; a missing one throws <see cref="DirectoryNotFoundException"/>.</param>
    /// <param name="target">The working list new models are appended to.</param>
    private void ReadFileSystem(string path, List<ModelProfile> target)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*.rcfg", SearchOption.AllDirectories)
            .ToList();

        // Per-file try/catch so one malformed model doesn't abort loading the rest.
        foreach (string file in files)
        {
            try
            {
                Dictionary<string, string> dict = RConfigParser.Read(file);
                // Subfolders are organizational only; the declared name is the registered name.
                ModelProfile? model = RConfigParser.ToObject<ModelProfile>(dict);

                if (model?.Name is null)
                    continue;

                model.ResolveProvider(_providers);
                CheckAdd(target, model, embedded: false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"ModelManager: Failed to load '{file}': {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public void LoadAssembly(Assembly assembly) => Merge(loaded => ReadEmbeddedResources(assembly, loaded));

    /// <summary>Reads the model profiles embedded in <paramref name="assembly"/> into <paramref name="target"/>.</summary>
    /// <param name="assembly">The assembly whose <c>.Models.Inference.</c> resources are read.</param>
    /// <param name="target">The working list new models are appended to.</param>
    private void ReadEmbeddedResources(Assembly assembly, List<ModelProfile> target)
    {
        try
        {
            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Models.Inference.") &&
                            n.EndsWith(".rcfg", StringComparison.InvariantCultureIgnoreCase));

            // Per-resource try/catch so one malformed model doesn't abort loading the rest.
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
                    ModelProfile? model = RConfigParser.ToObject<ModelProfile>(dict);

                    if (model?.Name is null)
                        continue;

                    model.ResolveProvider(_providers);
                    CheckAdd(target, model, embedded: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"ModelManager: Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading models from embedded resources: {e.Message}");
        }
    }

    /// <summary>Appends <paramref name="model"/> to <paramref name="target"/> unless a model of that name is already there.</summary>
    /// <param name="target">The working list being built.</param>
    /// <param name="model">The model just read.</param>
    /// <param name="embedded">Whether it came from an embedded resource (for the log line).</param>
    private void CheckAdd(List<ModelProfile> target, ModelProfile model, bool embedded)
    {
        if (target.Any(m => m.Name == model.Name))
            return;

        target.Add(model);
        _logger.LogInfo(embedded
            ? $"Loaded embedded model \"{model.Name}\""
            : $"Loaded model \"{model.Name}\" from file system");
    }
}
