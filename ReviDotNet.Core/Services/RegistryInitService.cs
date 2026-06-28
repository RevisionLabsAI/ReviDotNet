// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace Revi;

/// <summary>
/// Hosted service that initializes all registry services at application startup.
/// Registered automatically by <see cref="ReviServiceCollectionExtensions.AddReviDotNet"/>;
/// do not register manually.
/// </summary>
internal sealed class RegistryInitService : IHostedService
{
    private readonly IProviderManager _providers;
    private readonly IModelManager _models;
    private readonly IEmbeddingManager _embeddings;
    private readonly IPromptManager _prompts;
    private readonly IToolManager _tools;
    private readonly IAgentManager _agents;
    private readonly Assembly _appAssembly;
    private readonly ReviRegistryOptions _options;
    private readonly IReviLogger<RegistryInitService> _logger;

    /// <summary>Initializes the <see cref="RegistryInitService"/> with all registry services.</summary>
    public RegistryInitService(
        IProviderManager providers,
        IModelManager models,
        IEmbeddingManager embeddings,
        IPromptManager prompts,
        IToolManager tools,
        IAgentManager agents,
        Assembly appAssembly,
        ReviRegistryOptions options,
        IReviLogger<RegistryInitService> logger)
    {
        _providers = providers;
        _models = models;
        _embeddings = embeddings;
        _prompts = prompts;
        _tools = tools;
        _agents = agents;
        _appAssembly = appAssembly;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInfo($"Initializing Revi registries from assembly {_appAssembly.FullName}");

            await _providers.LoadAsync(_appAssembly, cancellationToken);
            await _models.LoadAsync(_appAssembly, cancellationToken);
            await _embeddings.LoadAsync(_appAssembly, cancellationToken);
            await _prompts.LoadAsync(_appAssembly, cancellationToken);
            await _tools.LoadAsync(_appAssembly, cancellationToken);
            await _agents.LoadAsync(_appAssembly, cancellationToken);

            LoadAdditionalConfigDirectories();

            ForgeManager.Load();

            _logger.LogInfo("Revi registries initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to initialize Revi registries", object1: ex);
            throw;
        }
    }

    /// <summary>
    /// Loads any extra RConfig folders configured via
    /// <see cref="ReviRegistryOptions.AdditionalConfigDirectories"/>. Each is treated as an RConfigs root;
    /// types are loaded grouped (all folders' providers first, then models, embeddings, prompts, agents) so a
    /// model in one folder can resolve a provider declared in another. Missing/invalid folders are skipped
    /// with a warning rather than aborting startup.
    /// </summary>
    private void LoadAdditionalConfigDirectories()
    {
        if (_options.AdditionalConfigDirectories.Count == 0) return;

        var resolved = new List<string>();
        foreach (string dir in _options.AdditionalConfigDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            string full;
            try { full = Path.GetFullPath(dir); }
            catch (Exception ex)
            {
                _logger.LogWarning($"Skipping additional RConfig path '{dir}': {ex.Message}");
                continue;
            }

            if (!Directory.Exists(full))
            {
                _logger.LogWarning($"Additional RConfig folder not found, skipping: {full}");
                continue;
            }

            resolved.Add(full);
            _logger.LogInfo($"Loading additional RConfigs from: {full}");
        }

        if (resolved.Count == 0) return;

        foreach (string dir in resolved) _providers.LoadDirectory(dir);
        foreach (string dir in resolved) _models.LoadDirectory(dir);
        foreach (string dir in resolved) _embeddings.LoadDirectory(dir);
        foreach (string dir in resolved) _prompts.LoadDirectory(dir);
        foreach (string dir in resolved) _tools.LoadDirectory(dir);
        foreach (string dir in resolved) _agents.LoadDirectory(dir);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
