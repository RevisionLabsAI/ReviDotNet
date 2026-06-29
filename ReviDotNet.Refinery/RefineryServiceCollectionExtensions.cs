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
    /// Registers the Refinery engine services (capture broker + publisher decorator, judge, runner,
    /// controller, in-memory store).
    /// <para>
    /// <b>Call AFTER the host registers its own <see cref="IRlogEventPublisher"/></b>: this DECORATES the
    /// existing publisher with a <see cref="CompositeRlogPublisher"/> that also feeds the capture broker, so
    /// the host's logging/observability is preserved (additive, not replaced). The host must also ensure
    /// this assembly's embedded RConfigs (the evaluator prompts) are loaded into its ReviDotNet registry,
    /// and call <c>ReviServiceLocator.SetProvider(provider)</c> after building so agent-run events reach the
    /// decorator.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRefinery(this IServiceCollection services)
    {
        services.AddSingleton<RefineryCaptureBroker>();

        // Decorate any already-registered IRlogEventPublisher (else a no-op inner) with the composite.
        ServiceDescriptor? prior = services.LastOrDefault(d => d.ServiceType == typeof(IRlogEventPublisher));
        services.AddSingleton<IRlogEventPublisher>(sp =>
            new CompositeRlogPublisher(ResolveInner(prior, sp), sp.GetRequiredService<RefineryCaptureBroker>()));

        // Meta-LLM usage broker: a single instance whose per-async-flow state isolates concurrent campaigns.
        // Constructor-injected into the three meta-LLM drivers (judge / pairwise / proposer) so their token
        // usage can be accounted against the per-campaign meta budget.
        services.AddSingleton<MetaLlmUsageBroker>();

        services.AddSingleton<ILlmJudge, LlmJudge>();
        services.AddSingleton<RefinementRunner>();

        // Phase 4 loop dependencies. The proposer, pairwise gate, and candidate validator are stateless
        // singletons; the BudgetGovernor is per-campaign (`new` inside RunCampaignAsync), NOT a DI singleton.
        services.AddSingleton<IProposalStrategy, LlmDiffProposer>();
        services.AddSingleton<PairwiseGate>();
        services.AddSingleton<CandidateValidator>();

        services.AddSingleton<RefinementController>();
        services.AddSingleton<MetaAnalyzer>();

        // Analysis services: calibration reporting + scenario generation. Stateless singletons.
        services.AddSingleton<CalibrationAnalyzer>();
        services.AddSingleton<ScenarioGenerator>();
        services.AddSingleton<IScenarioGenerator>(sp => sp.GetRequiredService<ScenarioGenerator>());

        if (services.All(d => d.ServiceType != typeof(ICampaignStore)))
            services.AddSingleton<ICampaignStore, InMemoryCampaignStore>();

        return services;
    }

    private static IRlogEventPublisher ResolveInner(ServiceDescriptor? prior, IServiceProvider sp)
    {
        if (prior is null)
            return new NullRlogSink();
        if (prior.ImplementationInstance is IRlogEventPublisher instance)
            return instance;
        if (prior.ImplementationFactory is { } factory && factory(sp) is IRlogEventPublisher fromFactory)
            return fromFactory;
        if (prior.ImplementationType is { } type)
            return (IRlogEventPublisher)ActivatorUtilities.CreateInstance(sp, type);
        return new NullRlogSink();
    }

    private sealed class NullRlogSink : IRlogEventPublisher
    {
        public Task PublishLogEventAsync(RlogEvent rlogEvent) => Task.CompletedTask;
        public void PublishLogEvent(RlogEvent rlogEvent) { }
    }
}
