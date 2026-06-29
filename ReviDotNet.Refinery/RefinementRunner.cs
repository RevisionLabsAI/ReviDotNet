// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;

namespace Revi.Refinery;

/// <summary>
/// Runs a registered agent once and returns its result plus a captured <see cref="AgentTrace"/>. Capture is
/// scoped per-run via <see cref="RefineryCaptureBroker"/> (async-context isolation), so concurrent runs do
/// not cross-contaminate and a run's sub-agents are included. Requires the host to have registered the
/// <see cref="CompositeRlogPublisher"/> (done by <c>AddRefinery</c>) and called
/// <c>ReviServiceLocator.SetProvider</c>.
/// </summary>
public sealed class RefinementRunner(IAgentService agents, RefineryCaptureBroker broker)
{
    private readonly IAgentService _agents = agents;
    private readonly RefineryCaptureBroker _broker = broker;

    /// <summary>Run the agent once with the given inputs, capturing its trace.</summary>
    public async Task<AgentRun> RunOnceAsync(
        string agentName, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        Dictionary<string, object> dict = inputs.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

        using RefineryCaptureBroker.CaptureScope scope = _broker.BeginCapture();
        Stopwatch sw = Stopwatch.StartNew();
        AgentResult result = await _agents.Run(agentName, dict, ct);
        sw.Stop();

        AgentTrace trace = AgentTraceBuilder.Build(scope.Capture.Events, agentName, result);
        return new AgentRun(result, trace, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Run a profile DIRECTLY once with the given inputs, capturing its trace. Mirrors the name-based
    /// <see cref="RunOnceAsync(string, IReadOnlyDictionary{string, string}, CancellationToken)"/> but uses the
    /// additive <see cref="IAgentService.Run(AgentProfile, IReadOnlyDictionary{string, object}, AgentRunContext?, CancellationToken, IToolManager?, ModelProfile?)"/>
    /// overload so no shared <see cref="IAgentManager"/> registry slot is mutated. When supplied,
    /// <paramref name="tools"/> isolates this run's tool registry and <paramref name="model"/> overrides model
    /// resolution — enabling per-run isolation (e.g. concurrent Refinery candidates).
    /// </summary>
    public async Task<AgentRun> RunOnceAsync(
        AgentProfile profile,
        IReadOnlyDictionary<string, string> inputs,
        IToolManager? tools = null,
        ModelProfile? model = null,
        CancellationToken ct = default)
    {
        Dictionary<string, object> dict = inputs.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

        using RefineryCaptureBroker.CaptureScope scope = _broker.BeginCapture();
        Stopwatch sw = Stopwatch.StartNew();
        AgentResult result = await _agents.Run(profile, dict, context: null, token: ct, toolOverride: tools, modelOverride: model);
        sw.Stop();

        AgentTrace trace = AgentTraceBuilder.Build(scope.Capture.Events, profile.Name ?? "", result);
        return new AgentRun(result, trace, sw.ElapsedMilliseconds);
    }
}

/// <summary>The outcome of a single agent run: the raw result, the typed trace, and wall-clock latency.</summary>
/// <param name="Result">The agent result (final output, exit reason, steps, session, cost).</param>
/// <param name="Trace">The captured, typed trace.</param>
/// <param name="LatencyMs">Wall-clock duration of the run.</param>
public sealed record AgentRun(AgentResult Result, AgentTrace Trace, long LatencyMs);
