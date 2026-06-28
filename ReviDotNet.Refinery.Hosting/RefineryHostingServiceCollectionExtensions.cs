// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Revi.Refinery.Hosting;

/// <summary>DI registration for the Refinery plugin host (build + load local repos).</summary>
public static class RefineryHostingServiceCollectionExtensions
{
    /// <summary>Register the plugin host, binding options from the <c>Refinery</c> configuration section.</summary>
    public static IServiceCollection AddRefineryHosting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RefineryHostingOptions>(o =>
            configuration.GetSection(RefineryHostingOptions.SectionName).Bind(o));
        return AddCore(services);
    }

    /// <summary>Register the plugin host with options configured in code.</summary>
    public static IServiceCollection AddRefineryHosting(this IServiceCollection services, Action<RefineryHostingOptions> configure)
    {
        services.Configure(configure);
        return AddCore(services);
    }

    private static IServiceCollection AddCore(IServiceCollection services)
    {
        services.AddSingleton<PluginBuilder>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<PluginManager>();
        services.AddHostedService<PluginHostInitializer>();
        return services;
    }
}

/// <summary>Builds + loads all configured plugins at startup when <see cref="RefineryHostingOptions.BuildOnStartup"/> is set.</summary>
internal sealed class PluginHostInitializer(
    PluginManager manager,
    IOptions<RefineryHostingOptions> options,
    ILogger<PluginHostInitializer>? log = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.BuildOnStartup)
            return;
        try
        {
            await manager.RefreshAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Refinery plugin refresh on startup failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
