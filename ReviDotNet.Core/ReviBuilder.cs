// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Revi;

/// <summary>
/// Fluent builder for creating a standalone <see cref="ReviClient"/> without a .NET host.
/// </summary>
/// <example>
/// <code>
/// await using ReviClient revi = await ReviBuilder.Create()
///     .WithAssembly(Assembly.GetEntryAssembly())
///     .BuildAsync();
/// </code>
/// </example>
public sealed class ReviBuilder
{
    private Assembly? _assembly;

    /// <summary>Creates a new <see cref="ReviBuilder"/>. Shorthand for <c>new ReviBuilder()</c>.</summary>
    public static ReviBuilder Create() => new();

    /// <summary>
    /// Specifies the application assembly used to locate embedded RConfig resources.
    /// Defaults to <see cref="Assembly.GetEntryAssembly()"/> when not set.
    /// </summary>
    /// <param name="assembly">The assembly to scan for embedded resources.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public ReviBuilder WithAssembly(Assembly? assembly)
    {
        _assembly = assembly;
        return this;
    }

    /// <summary>
    /// Builds and initializes a <see cref="ReviClient"/> by resolving all services and running
    /// the registry startup sequence.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel initialization.</param>
    /// <returns>A fully initialized <see cref="ReviClient"/>.</returns>
    public async Task<ReviClient> BuildAsync(CancellationToken cancellationToken = default)
    {
        ServiceCollection services = [];
        services.AddReviDotNet(_assembly);

        ServiceProvider provider = services.BuildServiceProvider();

        // Run all registered IHostedService instances (only RegistryInitService at present)
        IEnumerable<IHostedService> hostedServices = provider.GetServices<IHostedService>();
        foreach (IHostedService svc in hostedServices)
            await svc.StartAsync(cancellationToken);

        return new ReviClient(provider);
    }
}
