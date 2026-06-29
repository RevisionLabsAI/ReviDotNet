// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Globalization;
using System.Text.RegularExpressions;

namespace Revi.Refinery;

/// <summary>
/// Typed mutator for the <c>guardrail</c> knob. When the round shows termination/loop failures — the agent
/// hitting its step ceiling before completing — raises <c>max-steps</c> by ~25% (at least +1) on the first
/// guardrail line, giving the agent the headroom to finish. Returns <c>null</c> when there is no
/// <c>max-steps</c> line or when no termination/loop weakness is evident.
/// </summary>
public sealed partial class GuardrailMutator : ICandidateMutator
{
    /// <inheritdoc/>
    public string KnobType => "guardrail";

    /// <inheritdoc/>
    public Proposal? Mutate(string agentName, string currentDefinition, SuiteAggregate scores, IReadOnlyList<ScoreCard> cards)
    {
        if (string.IsNullOrEmpty(currentDefinition))
            return null;

        if (!HasTerminationOrLoopFailure(cards))
            return null;

        Match m = MaxStepsLine().Match(currentDefinition);
        if (!m.Success)
            return null; // knob absent

        if (!int.TryParse(m.Groups["val"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int current) || current < 0)
            return null;

        int revisedVal = current + Math.Max(1, (int)Math.Ceiling(current * 0.25));
        if (revisedVal <= current)
            return null;

        string revisedLine = $"{m.Groups["pre"].Value}{revisedVal.ToString(CultureInfo.InvariantCulture)}";
        string revised = currentDefinition.Remove(m.Index, m.Length).Insert(m.Index, revisedLine);
        if (revised == currentDefinition)
            return null;

        return new Proposal(
            KnobType,
            revised,
            MutatorText.Diff(currentDefinition, revised),
            $"Raise max-steps {current} → {revisedVal} so '{agentName}' has headroom to terminate cleanly.");
    }

    // True when any card shows a step-limit/loop/termination style failure (outcome text or invariant evidence).
    private static bool HasTerminationOrLoopFailure(IReadOnlyList<ScoreCard> cards)
    {
        foreach (ScoreCard card in cards)
        {
            if (LooksLikeTermination(card.Outcome))
                return true;

            foreach (InvariantResult inv in card.Invariants)
            {
                if (!inv.Passed && (LooksLikeTermination(inv.Evidence) || LooksLikeTermination(inv.Id)))
                    return true;
            }
        }

        return false;
    }

    private static bool LooksLikeTermination(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return TerminationSignal().IsMatch(text);
    }

    // Matches a "max-steps = 6" style line, capturing the key+separator ("pre") and the integer value.
    [GeneratedRegex(@"(?<pre>^[ \t]*max-steps[ \t]*=[ \t]*)(?<val>\d+)", RegexOptions.Multiline)]
    private static partial Regex MaxStepsLine();

    // Words that signal the agent ran out of steps / looped / failed to terminate.
    [GeneratedRegex(@"max[ _-]?steps|step limit|step ceiling|loop|did not terminate|never terminat|non[- ]?termination|ran out of steps|exhausted steps|incomplete", RegexOptions.IgnoreCase)]
    private static partial Regex TerminationSignal();
}
