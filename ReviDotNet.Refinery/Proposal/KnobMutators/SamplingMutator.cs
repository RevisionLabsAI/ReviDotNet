// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Globalization;
using System.Text.RegularExpressions;

namespace Revi.Refinery;

/// <summary>
/// Typed mutator for the <c>sampling</c> knob. When the round shows invariant or quality weakness, nudges
/// the agent's <c>temperature</c> DOWN by 0.1 (floored at 0.0) so the agent answers more deterministically.
/// Returns <c>null</c> when there is no <c>temperature</c> line, when temperature is already 0.0, or when the
/// round shows no weakness to fix.
/// </summary>
public sealed partial class SamplingMutator : ICandidateMutator
{
    private const double Step = 0.1;

    /// <inheritdoc/>
    public string KnobType => "sampling";

    /// <inheritdoc/>
    public Proposal? Mutate(string agentName, string currentDefinition, SuiteAggregate scores, IReadOnlyList<ScoreCard> cards)
    {
        if (string.IsNullOrEmpty(currentDefinition))
            return null;

        // Only act when there's a weakness a lower temperature could plausibly address.
        bool hasInvariantIssue = scores.InvariantPassRate < 1.0 || cards.Any(c => c.Verdict == RunVerdict.Fail);
        bool hasQualityIssue = cards.Any(c => c.Quality is not null) && scores.QualityMean < 8.0;
        if (!hasInvariantIssue && !hasQualityIssue)
            return null;

        Match m = TemperatureLine().Match(currentDefinition);
        if (!m.Success)
            return null; // knob absent

        if (!double.TryParse(m.Groups["val"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double current))
            return null; // malformed value — be conservative

        double revisedVal = Math.Max(0.0, Math.Round(current - Step, 2));
        if (revisedVal >= current)
            return null; // already optimal (0.0) — no useful change

        string revisedNumber = revisedVal.ToString("0.0#", CultureInfo.InvariantCulture);
        string revisedLine = $"{m.Groups["pre"].Value}{revisedNumber}";

        string revised = currentDefinition.Remove(m.Index, m.Length).Insert(m.Index, revisedLine);
        if (revised == currentDefinition)
            return null;

        string currentNumber = current.ToString("0.0#", CultureInfo.InvariantCulture);
        return new Proposal(
            KnobType,
            revised,
            MutatorText.Diff(currentDefinition, revised),
            $"Lower temperature {currentNumber} → {revisedNumber} for more deterministic answers on '{agentName}'.");
    }

    // Matches a "temperature = 0.7" style line, capturing the key+separator ("pre") and the numeric value.
    [GeneratedRegex(@"(?<pre>^[ \t]*temperature[ \t]*=[ \t]*)(?<val>\d+(?:\.\d+)?)", RegexOptions.Multiline)]
    private static partial Regex TemperatureLine();
}
