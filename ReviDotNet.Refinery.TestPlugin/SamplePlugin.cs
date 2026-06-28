// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Revi;
using Revi.Refinery;

namespace ReviDotNet.Refinery.TestPlugin;

/// <summary>A trivial refinement plugin for host-loading tests.</summary>
public sealed class SamplePlugin : IRefinementPlugin
{
    public string Name => "test-plugin";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // no services for the sample
    }

    public IEnumerable<IBuiltInTool> CreateTools(IServiceProvider services) => [];

    public IEnumerable<RefinableAgent> GetAgents() => [new("sample-agent", Description: "sample")];

    public IEnumerable<ScenarioSuite> GetScenarioSuites() =>
    [
        new()
        {
            Name = "sample",
            AgentName = "sample-agent",
            Scenarios = [new() { Id = "s1", AgentName = "sample-agent" }]
        }
    ];

    public IEnumerable<IInvariantChecker> GetInvariantCheckers() => [new AlwaysPasses()];
}

/// <summary>An invariant that always passes — exercises the Sdk type boundary.</summary>
internal sealed class AlwaysPasses : IInvariantChecker
{
    public string Id => "OK-1";
    public string Description => "always passes";
    public InvariantSeverity Severity => InvariantSeverity.Low;
    public InvariantResult Check(AgentTrace trace, Scenario scenario) => InvariantResult.Pass(this, "ok");
}
