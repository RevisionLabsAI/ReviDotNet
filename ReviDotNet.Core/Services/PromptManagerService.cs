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

            foreach (string file in files)
                LoadPromptFromFile(file, path);
        }
        catch (DirectoryNotFoundException)
        {
            LoadFromEmbeddedResources(assembly);
        }
        catch (Exception e)
        {
            _logger.LogError($"Error loading prompts: {e.Message}");
        }

        return Task.CompletedTask;
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
        string basePath = Path.GetDirectoryName(filePath)! + Path.DirectorySeparatorChar;
        LoadPromptFromFile(filePath, basePath);
    }

    private void LoadPromptFromFile(string file, string basePath)
    {
        Dictionary<string, string> dict = RConfigParser.Read(file);
        string folder = Util.ExtractSubDirectories(basePath, file).ToLower();
        Prompt? prompt = Prompt.ToObject(dict, folder);

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
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using StreamReader reader = new(stream);
                Dictionary<string, string> dict = RConfigParser.ReadEmbedded(reader.ReadToEnd());
                string folder = Util.ExtractEmbeddedDirectories(".Prompts.", resourceName).ToLower();
                Prompt? prompt = Prompt.ToObject(dict, folder);

                if (prompt?.Name is null)
                    continue;

                CheckAdd(prompt, embedded: true);
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
