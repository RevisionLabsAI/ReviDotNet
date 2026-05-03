// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Revi;

/// <summary>
/// Extension methods for registering ReviDotNet services with <see cref="IServiceCollection"/>.
/// </summary>
public static class ReviServiceCollectionExtensions
{
    /// <summary>
    /// Registers all ReviDotNet services: registry managers, primary inference/agent/embed services,
    /// logging, and the startup initializer. Call this once from your host's <c>ConfigureServices</c>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="appAssembly">
    /// The application assembly used to locate embedded RConfig resources.
    /// Defaults to <see cref="Assembly.GetEntryAssembly()"/> when null.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddReviDotNet(
        this IServiceCollection services,
        Assembly? appAssembly = null)
    {
        Assembly resolvedAssembly = appAssembly ?? Assembly.GetEntryAssembly()!;

        // Logging — use TryAdd so callers can substitute their own implementations
        services.TryAddSingleton<IReviLogger, ReviLogger>();
        services.TryAddSingleton(typeof(IReviLogger<>), typeof(ReviLogger<>));

        // Registry managers (singletons that hold loaded configs)
        services.AddSingleton<IProviderManager, ProviderManagerService>();
        services.AddSingleton<IModelManager, ModelManagerService>();
        services.AddSingleton<IEmbeddingManager, EmbeddingManagerService>();
        services.AddSingleton<IPromptManager, PromptManagerService>();
        services.AddSingleton<IToolManager, ToolManagerService>();
        services.AddSingleton<IAgentManager, AgentManagerService>();

        // Primary service interfaces
        services.AddSingleton<IInferService, InferService>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IEmbedService, EmbedService>();

        // Startup initializer — hidden behind IHostedService; callers never interact with it directly
        services.AddHostedService(sp =>
            ActivatorUtilities.CreateInstance<RegistryInitService>(sp, resolvedAssembly));

        return services;
    }
}
