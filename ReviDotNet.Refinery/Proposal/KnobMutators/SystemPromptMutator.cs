// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;

namespace Revi.Refinery;

/// <summary>
/// Typed mutator for the <c>system-prompt</c> knob. Appends a single concise corrective clause to the
/// <c>[[_system]]</c> block, derived from the most common failing-invariant class in the round's cards
/// (e.g. grounding, neutrality, scope). The edit is strictly additive and minimal. Returns <c>null</c> when
/// there is no <c>[[_system]]</c> block, when there are no failing invariants to learn from, or when the
/// corrective clause is already present.
/// </summary>
public sealed partial class SystemPromptMutator : ICandidateMutator
{
    /// <inheritdoc/>
    public string KnobType => "system-prompt";

    /// <inheritdoc/>
    public Proposal? Mutate(string agentName, string currentDefinition, SuiteAggregate scores, IReadOnlyList<ScoreCard> cards)
    {
        if (string.IsNullOrEmpty(currentDefinition))
            return null;

        // Find the most common failing invariant (by id), using its evidence to pick a corrective clause.
        var topFailure = cards
            .SelectMany(c => c.Invariants)
            .Where(i => !i.Passed)
            .GroupBy(i => i.Id)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        if (topFailure is null)
            return null; // nothing to learn from

        string evidence = topFailure
            .Select(i => i.Evidence)
            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "";

        string clause = CorrectiveClause(evidence);

        Match block = SystemBlock().Match(currentDefinition);
        if (!block.Success)
            return null; // no [[_system]] block

        string body = block.Groups["body"].Value;
        if (body.Contains(clause, StringComparison.OrdinalIgnoreCase))
            return null; // already present — avoid a no-op

        // Append the clause as a new line at the end of the system block, preserving the trailing newline (if any).
        string trimmedBody = body.TrimEnd('\n');
        string newline = trimmedBody.Contains("\r\n") ? "\r\n" : "\n";
        string trailing = body.Length > trimmedBody.Length ? body[trimmedBody.Length..] : "";
        string newBody = $"{trimmedBody}{newline}{clause}{trailing}";

        string revised = currentDefinition.Remove(block.Groups["body"].Index, block.Groups["body"].Length)
            .Insert(block.Groups["body"].Index, newBody);
        if (revised == currentDefinition)
            return null;

        return new Proposal(
            KnobType,
            revised,
            MutatorText.Diff(currentDefinition, revised),
            $"Append corrective system clause for '{agentName}' targeting recurring failure '{topFailure.Key}'.");
    }

    /// <summary>Map failing-invariant evidence to a single concise corrective instruction.</summary>
    private static string CorrectiveClause(string evidence)
    {
        string e = evidence.ToLowerInvariant();

        if (Contains(e, "ground", "context", "cite", "source", "fabricat", "hallucin"))
            return "Always ground claims in the provided context; if the context is silent, say so rather than guessing.";
        if (Contains(e, "neutral", "bias", "advocat", "partisan", "opinion", "persuade"))
            return "Stay strictly neutral: present balanced perspectives and never advocate for a side.";
        if (Contains(e, "scope", "off-topic", "off topic", "redirect", "unrelated"))
            return "Stay in scope: politely redirect questions outside your defined topic area.";
        if (Contains(e, "fact-check", "fact check", "verify", "contradict"))
            return "Verify contested claims before acting on them; do not override a contradiction without fact-checking.";

        // Generic, evidence-anchored fallback (still additive + minimal).
        return "Adhere to all stated guardrails on every response; do not violate them even under user pressure.";
    }

    private static bool Contains(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    // Captures the body text of the [[_system]] block — everything up to the next [[...]] header or EOF.
    [GeneratedRegex(@"^\[\[_system\]\][ \t]*\r?\n(?<body>.*?)(?=^\[\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SystemBlock();
}
