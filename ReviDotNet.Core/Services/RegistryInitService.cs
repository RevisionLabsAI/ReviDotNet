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
        IReviLogger<RegistryInitService> logger)
    {
        _providers = providers;
        _models = models;
        _embeddings = embeddings;
        _prompts = prompts;
        _tools = tools;
        _agents = agents;
        _appAssembly = appAssembly;
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

            ForgeManager.Load();

            _logger.LogInfo("Revi registries initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to initialize Revi registries", object1: ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
