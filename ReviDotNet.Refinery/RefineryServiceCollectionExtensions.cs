// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.DependencyInjection;

namespace Revi.Refinery;

/// <summary>DI registration for the Refinery engine.</summary>
public static class RefineryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Refinery engine services (trace capture, judge, runner, controller, in-memory store).
    /// <para>
    /// The caller must also include this assembly when loading RConfigs (e.g.
    /// <c>AddReviDotNet(typeof(Program).Assembly, typeof(RefineryServiceCollectionExtensions).Assembly)</c>)
    /// so the embedded evaluator prompts are available, and call
    /// <c>ReviServiceLocator.SetProvider(provider)</c> after building so captured traces flow to the
    /// registered <see cref="CapturingRlogPublisher"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRefinery(this IServiceCollection services)
    {
        services.AddSingleton<CapturingRlogPublisher>();
        services.AddSingleton<IRlogEventPublisher>(sp => sp.GetRequiredService<CapturingRlogPublisher>());
        services.AddSingleton<ILlmJudge, LlmJudge>();
        services.AddSingleton<RefinementRunner>();
        services.AddSingleton<RefinementController>();
        services.TryAddSingletonStore();
        return services;
    }

    private static void TryAddSingletonStore(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(ICampaignStore)))
            services.AddSingleton<ICampaignStore, InMemoryCampaignStore>();
    }
}
