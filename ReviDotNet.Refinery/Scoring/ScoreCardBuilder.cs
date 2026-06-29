// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;

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
            SessionId = trace.SessionId,
            FactCheckerDetermination = ParseDetermination(trace.FinalOutput)
        };

    /// <summary>
    /// Best-effort parse of a fact-checker determination from a run's final output. Scans for the first
    /// <c>{ … }</c> object and reads <c>winner</c>/<c>confidence</c>/<c>rationale</c>. Returns null unless a
    /// non-empty winner string is found, so non-fact-checker runs leave the field null. Never throws.
    /// </summary>
    private static FactCheckerDetermination? ParseDetermination(string? finalOutput)
    {
        if (string.IsNullOrWhiteSpace(finalOutput))
            return null;

        int start = finalOutput.IndexOf('{');
        int end = finalOutput.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        string json = finalOutput.Substring(start, end - start + 1);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? winner = ReadString(root, "winner");
            if (string.IsNullOrWhiteSpace(winner))
                return null;

            return new FactCheckerDetermination
            {
                Winner = winner,
                Confidence = ReadInt(root, "confidence"),
                Rationale = ReadString(root, "rationale")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el))
            return 0;

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out int n) => n,
            JsonValueKind.String when int.TryParse(el.GetString(), out int s) => s,
            _ => 0
        };
    }
}
