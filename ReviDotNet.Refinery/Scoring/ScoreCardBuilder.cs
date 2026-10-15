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
    /// <summary>
    /// Assemble a score card. <paramref name="agentNameOverride"/> replaces the trace's agent name on the card
    /// — used for candidate variant runs, whose trace runs under a transient "__refinery/…/candidate" profile
    /// name, so the card is filed under the REAL agent (with <paramref name="agentVersion"/> = the variant id)
    /// where calibration/analysis can find it. Baseline runs pass null and keep the trace's real agent name.
    /// </summary>
    public static ScoreCard Build(
        Scenario scenario,
        AgentTrace trace,
        IReadOnlyList<InvariantResult> invariants,
        QualityScore? quality,
        EfficiencyMetrics? efficiency,
        int sampleIndex,
        string mode,
        string? agentVersion = null,
        string? agentNameOverride = null) => new()
        {
            ScenarioId = scenario.Id,
            AgentName = agentNameOverride ?? trace.AgentName,
            AgentVersion = agentVersion,
            Mode = mode,
            SampleIndex = sampleIndex,
            Outcome = trace.ExitReason,
            Invariants = invariants,
            Quality = quality,
            QualityExpected = scenario.Rubric.Count > 0 && !FailedBeforeModel(trace),
            FailedBeforeModel = FailedBeforeModel(trace),
            Efficiency = efficiency,
            SessionId = trace.SessionId,
            FactCheckerDetermination = ParseDetermination(trace.FinalOutput)
        };

    /// <summary>
    /// Whether a run ended in an error before any model call answered: it did not complete and reported no
    /// tokens. Such a run (a revoked key, an account out of credit) has nothing a judge could grade.
    /// </summary>
    /// <param name="trace">The run's trace.</param>
    /// <returns>True when the run never reached a model.</returns>
    public static bool FailedBeforeModel(AgentTrace trace) =>
        !trace.Completed && trace.InputTokens == 0 && trace.OutputTokens == 0;

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
