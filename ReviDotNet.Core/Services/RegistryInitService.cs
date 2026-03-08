// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace Revi;

/// <summary>
/// Initializes Revi registries at app startup using the existing static loader methods.
/// This allows the rest of the app to consume the registries via DI interfaces.
/// </summary>
public sealed class RegistryInitService : IHostedService
{
    private readonly IReviLogger<RegistryInitService> _logger;
    private readonly Assembly? _appAssembly;

    // Backward-compatible constructor
    public RegistryInitService(IReviLogger<RegistryInitService> logger)
    {
        _logger = logger;
    }

    // Preferred constructor: accept the launching application's assembly
    public RegistryInitService(IReviLogger<RegistryInitService> logger, Assembly appAssembly)
    {
        _logger = logger;
        _appAssembly = appAssembly;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Prefer the assembly provided by the hosting application; fall back to this library's assembly
            Assembly assembly = _appAssembly ?? typeof(RegistryInitService).Assembly;
            _logger.LogInfo($"Initializing Revi registries from assembly {assembly.FullName}");

            ProviderManager.Load(assembly);
            ModelManager.Load(assembly);
            EmbeddingManager.Load(assembly);
            PromptManager.Load(assembly);

            _logger.LogInfo("Revi registries initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to initialize Revi registries", object1: ex);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
