// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>Service implementation of <see cref="IPromptManager"/>. Holds loaded prompts as instance state.</summary>
public sealed class PromptManagerService : IPromptManager
{
    private readonly List<Prompt> _prompts = [];
    private readonly IReviLogger<PromptManagerService> _logger;

    /// <summary>Initializes a new <see cref="PromptManagerService"/>.</summary>
    public PromptManagerService(IReviLogger<PromptManagerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default)
    {
        _prompts.Clear();

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
                    LoadPromptFromFile(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to load prompt '{file}': {ex.Message}");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            LoadFromEmbeddedResources(assembly);
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading prompts: {e.Message}");
        }

        // Overlay built-in default prompts (json-fixer, enum-fixer) shipped embedded in ReviDotNet.Core.
        // Runs last so any app-defined prompt of the same name (loaded above) wins; CheckAdd only fills gaps.
        LoadFromEmbeddedResources(typeof(PromptManagerService).Assembly);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void LoadDirectory(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "Prompts") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(path)) return;

        foreach (string file in Directory.EnumerateFiles(path, "*.pmt", SearchOption.AllDirectories))
        {
            try
            {
                LoadPromptFromFile(file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to load prompt '{file}': {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public Prompt? Get(string name)
        => _prompts.FirstOrDefault(p => p.Name == name);

    /// <inheritdoc/>
    public List<Prompt> GetAll()
        => [.._prompts];

    /// <inheritdoc/>
    public void AddOrUpdate(Prompt prompt)
        => CheckAdd(prompt, embedded: false);

    /// <inheritdoc/>
    public void LoadFromFile(string filePath)
    {
        LoadPromptFromFile(filePath);
    }

    /// <summary>
    /// Loads one .pmt file and registers it under its declared <c>[[information]] name</c> verbatim.
    /// Subfolders are organizational only: prefixing the name with a lowercased folder path (the old
    /// behavior, e.g. <c>evaluator/Evaluator.AgentRunJudge</c>) made every subfoldered prompt
    /// unreachable, because all lookups use the declared name (see <see cref="Get"/>).
    /// </summary>
    private void LoadPromptFromFile(string file)
    {
        Dictionary<string, string> dict = RConfigParser.Read(file);
        Prompt? prompt = Prompt.ToObject(dict);

        if (prompt?.Name is null)
            return;

        CheckAdd(prompt, embedded: false);
    }

    private void LoadFromEmbeddedResources(Assembly assembly)
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

                    CheckAdd(prompt, embedded: true);
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

    private void CheckAdd(Prompt newPrompt, bool embedded)
    {
        Prompt? existing = _prompts.FirstOrDefault(p => p.Name == newPrompt.Name);
        if (existing == null)
        {
            _prompts.Add(newPrompt);
            _logger.LogInfo(embedded
                ? $"Loaded embedded prompt \"{newPrompt.Name}\""
                : $"Loaded prompt \"{newPrompt.Name}\" from file system");
        }
        else if (newPrompt.Version > existing.Version)
        {
            _prompts[_prompts.IndexOf(existing)] = newPrompt;
            _logger.LogInfo($"Updated prompt \"{newPrompt.Name}\" to newer version");
        }
    }
}
