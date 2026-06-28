// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>Combines structural, quality, and efficiency results for one run into a <see cref="ScoreCard"/>.</summary>
public static class ScoreCardBuilder
{
    /// <summary>Assemble a score card.</summary>
    public static ScoreCard Build(
        Scenario scenario,
        AgentTrace trace,
        IReadOnlyList<InvariantResult> invariants,
        QualityScore? quality,
        EfficiencyMetrics? efficiency,
        int sampleIndex,
        string mode,
        string? agentVersion = null) => new()
        {
            ScenarioId = scenario.Id,
            AgentName = trace.AgentName,
            AgentVersion = agentVersion,
            Mode = mode,
            SampleIndex = sampleIndex,
            Outcome = trace.ExitReason,
            Invariants = invariants,
            Quality = quality,
            Efficiency = efficiency,
            SessionId = trace.SessionId
        };
}
