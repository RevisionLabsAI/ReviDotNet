// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Revi.Refinery;

/// <summary>
/// A refinement plugin contributed by a host application (e.g. "MyApp"). It registers the app's
/// runtime services and tools (bound to an ISOLATED test environment), and advertises the agents,
/// scenario suites, and invariant checkers the Refinery engine should evaluate and improve.
/// <para>
/// Implementations are discovered and instantiated by <c>ReviDotNet.Refinery.Hosting</c>. The host process
/// shares <c>ReviDotNet.Core</c> and <c>ReviDotNet.Refinery.Sdk</c> with the plugin so this contract type
/// has a single identity across the load-context boundary.
/// </para>
/// </summary>
public interface IRefinementPlugin
{
    /// <summary>Stable display/identifier name, e.g. "MyApp".</summary>
    string Name { get; }

    /// <summary>
    /// Register the application's services into a per-campaign DI scope — repositories bound to an
    /// ISOLATED test store, search/scrape configuration, etc. Never wire production data sinks here.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Create the agent tools (e.g. domain bridge tools), resolved from the configured scope.</summary>
    IEnumerable<IBuiltInTool> CreateTools(IServiceProvider services);

    /// <summary>The agents this plugin exposes for evaluation/refinement.</summary>
    IEnumerable<RefinableAgent> GetAgents();

    /// <summary>Scenario suites used to evaluate the agents.</summary>
    IEnumerable<ScenarioSuite> GetScenarioSuites();

    /// <summary>Structural invariant checkers (hard pass/fail gates) evaluated against each run's trace.</summary>
    IEnumerable<IInvariantChecker> GetInvariantCheckers();
}

/// <summary>An agent the plugin exposes for refinement.</summary>
/// <param name="Name">
/// The agent's effective name, as resolved by the host's ReviDotNet agent registry. The agent's
/// <c>.agent</c>/<c>.pmt</c> files must be loaded into that registry by the host (e.g. via the host's
/// RConfig paths / <c>REVI_RCONFIG_PATHS</c>); the engine resolves the agent by this name.
/// </param>
/// <param name="Description">Optional human description.</param>
public sealed record RefinableAgent(string Name, string? Description = null);
