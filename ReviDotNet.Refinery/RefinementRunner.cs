// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;

namespace Revi.Refinery;

/// <summary>
/// Runs a registered agent once and returns its result plus a captured <see cref="AgentTrace"/>.
/// Requires the host to have wired this runner's <see cref="CapturingRlogPublisher"/> as the process's
/// ReviLog publisher (via <c>ReviServiceLocator.SetProvider</c>). Because a single capture buffer is shared,
/// runs through one runner instance are serialized.
/// </summary>
public sealed class RefinementRunner(IAgentService agents, CapturingRlogPublisher capture)
{
    private readonly IAgentService _agents = agents;
    private readonly CapturingRlogPublisher _capture = capture;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Run the agent once with the given inputs, capturing its trace.</summary>
    public async Task<AgentRun> RunOnceAsync(
        string agentName, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _capture.Clear();
            Dictionary<string, object> dict = inputs.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

            Stopwatch sw = Stopwatch.StartNew();
            AgentResult result = await _agents.Run(agentName, dict, ct);
            sw.Stop();

            IReadOnlyList<RlogEvent> events = _capture.Drain();
            AgentTrace trace = AgentTraceBuilder.Build(events, agentName, result);
            return new AgentRun(result, trace, sw.ElapsedMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The outcome of a single agent run: the raw result, the typed trace, and wall-clock latency.</summary>
/// <param name="Result">The agent result (final output, exit reason, steps).</param>
/// <param name="Trace">The captured, typed trace.</param>
/// <param name="LatencyMs">Wall-clock duration of the run.</param>
public sealed record AgentRun(AgentResult Result, AgentTrace Trace, long LatencyMs);
