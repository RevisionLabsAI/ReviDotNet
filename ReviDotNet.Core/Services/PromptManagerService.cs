// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>
/// Service implementation of <see cref="IPromptManager"/>. Holds loaded prompts as instance state.
/// Reads never lock and are safe during a reload: every write builds a complete replacement list and publishes it
/// with one reference swap, so a reader sees either the old list or the new one, never a partial or empty one.
/// </summary>
public sealed class PromptManagerService : IPromptManager
{
    /// <summary>Serialises writers so concurrent loads and updates cannot lose each other's entries. Readers never take it.</summary>
    private readonly object _writeLock = new();
    /// <summary>The published prompts. Never mutated once assigned; writers replace the whole array under <see cref="_writeLock"/>.</summary>
    private volatile Prompt[] _prompts = [];
    private readonly IReviLogger<PromptManagerService> _logger;

    /// <summary>Initializes a new <see cref="PromptManagerService"/>.</summary>
    public PromptManagerService(IReviLogger<PromptManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        lock (_writeLock)
        {
            List<Prompt> loaded = [];
            try
            {
                string path = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Prompts/";
                List<string> files = Directory
                    .EnumerateFiles(path, "*.pmt", SearchOption.AllDirectories)
                    .ToList();

                // Per-file try/catch so one malformed prompt doesn't abort loading the rest.
                foreach (string file in files)
                {
                    try
                    {
                        ReadPromptFile(file, loaded);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to load prompt '{file}': {ex.Message}");
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                ReadEmbeddedResources(assembly, loaded);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error loading prompts: {e.Message}");
            }

            // Overlay built-in default prompts (json-fixer, enum-fixer) shipped embedded in ReviDotNet.Core.
            // Runs last so any app-defined prompt of the same name (loaded above) wins; CheckAdd only fills gaps.
            ReadEmbeddedResources(typeof(PromptManagerService).Assembly, loaded);

            _prompts = [.. loaded];
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void LoadDirectory(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "Prompts") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(path)) return;

        Merge(target =>
        {
            foreach (string file in Directory.EnumerateFiles(path, "*.pmt", SearchOption.AllDirectories))
            {
                try
                {
                    ReadPromptFile(file, target);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to load prompt '{file}': {ex.Message}");
                }
            }
        });
    }

    /// <inheritdoc/>
    public Prompt? Get(string name)
        => _prompts.FirstOrDefault(p => p.Name == name);

    /// <inheritdoc/>
    public List<Prompt> GetAll()
        => [.._prompts];

    /// <inheritdoc/>
    public void AddOrUpdate(Prompt prompt)
        => Merge(target => CheckAdd(target, prompt, embedded: false));

    /// <inheritdoc/>
    public void LoadFromFile(string filePath)
        => Merge(target => ReadPromptFile(filePath, target));

    /// <summary>Publishes the current prompts as changed by <paramref name="update"/>, as one swap.</summary>
    /// <param name="update">Adds or replaces prompts in the working copy it is given.</param>
    private void Merge(Action<List<Prompt>> update)
    {
        lock (_writeLock)
        {
            List<Prompt> merged = [.. _prompts];
            update(merged);
            _prompts = [.. merged];
        }
    }

    /// <summary>
    /// Reads one .pmt file into <paramref name="target"/> under its declared <c>[[information]] name</c> verbatim.
    /// Subfolders are organizational only: prefixing the name with a lowercased folder path (the old
    /// behavior, e.g. <c>evaluator/Evaluator.AgentRunJudge</c>) made every subfoldered prompt
    /// unreachable, because all lookups use the declared name (see <see cref="Get"/>).
    /// </summary>
    /// <param name="file">The .pmt file to read.</param>
    /// <param name="target">The working list the prompt is added to or updated in.</param>
    private void ReadPromptFile(string file, List<Prompt> target)
    {
        Dictionary<string, string> dict = RConfigParser.Read(file);
        Prompt? prompt = Prompt.ToObject(dict);

        if (prompt?.Name is null)
            return;

        CheckAdd(target, prompt, embedded: false);
    }

    /// <inheritdoc/>
    public void LoadAssembly(Assembly assembly) => Merge(target => ReadEmbeddedResources(assembly, target));

    /// <summary>Reads the prompts embedded in <paramref name="assembly"/> into <paramref name="target"/>.</summary>
    /// <param name="assembly">The assembly whose <c>.Prompts.</c> resources are read.</param>
    /// <param name="target">The working list prompts are added to or updated in.</param>
    private void ReadEmbeddedResources(Assembly assembly, List<Prompt> target)
    {
        try
        {
            IEnumerable<string> resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Prompts.") &&
                            n.EndsWith(".pmt", StringComparison.InvariantCultureIgnoreCase));

            foreach (string resourceName in resourceNames)
            {
                // Per-resource try/catch so one malformed prompt doesn't abort loading the rest.
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;

                    using StreamReader reader = new(stream);
                    Dictionary<string, string> dict = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                    // Register under the declared name verbatim (no folder prefix) — see LoadPromptFromFile.
                    Prompt? prompt = Prompt.ToObject(dict);

                    if (prompt?.Name is null)
                        continue;

                    CheckAdd(target, prompt, embedded: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to load embedded prompt '{resourceName}': {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading prompts from embedded resources: {e.Message}");
        }
    }

    /// <summary>
    /// Adds <paramref name="newPrompt"/> to <paramref name="target"/>, or replaces the entry of the same name in place
    /// when <paramref name="newPrompt"/> has a higher version.
    /// </summary>
    /// <param name="target">The working list being built.</param>
    /// <param name="newPrompt">The prompt just read or supplied.</param>
    /// <param name="embedded">Whether it came from an embedded resource (for the log line).</param>
    private void CheckAdd(List<Prompt> target, Prompt newPrompt, bool embedded)
    {
        Prompt? existing = target.FirstOrDefault(p => p.Name == newPrompt.Name);
        if (existing == null)
        {
            target.Add(newPrompt);
            _logger.LogInfo(embedded
                ? $"Loaded embedded prompt \"{newPrompt.Name}\""
                : $"Loaded prompt \"{newPrompt.Name}\" from file system");
        }
        else if (newPrompt.Version > existing.Version)
        {
            target[target.IndexOf(existing)] = newPrompt;
            _logger.LogInfo($"Updated prompt \"{newPrompt.Name}\" to newer version");
        }
    }
}
