// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>Extracts efficiency metrics (steps, tool calls, tokens, latency, cost) from a run's trace.</summary>
public static class EfficiencyExtractor
{
    /// <summary>Build <see cref="EfficiencyMetrics"/> from a trace and the measured wall-clock latency.</summary>
    public static EfficiencyMetrics Extract(AgentTrace trace, long latencyMs, decimal costUsd = 0m) => new()
    {
        TotalSteps = trace.TotalSteps,
        ToolCalls = trace.ToolCalls.Count(),
        InputTokens = trace.InputTokens,
        OutputTokens = trace.OutputTokens,
        CostUsd = costUsd,
        LatencyMs = latencyMs
    };
}
