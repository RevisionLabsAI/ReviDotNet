// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using Newtonsoft.Json;

namespace Revi.Refinery;

/// <summary>
/// Drives the <c>Evaluator.Proposer</c> prompt (pinned to a high-effort reasoning model) to propose the
/// single highest-leverage, minimal revision to an agent given the weaknesses surfaced by a scoring round.
/// Returns the full revised definition plus a computed unified line-diff vs the current one.
/// </summary>
public sealed class LlmDiffProposer(IInferService infer) : IProposalStrategy
{
    /// <summary>The proposer prompt name (shipped embedded in this assembly's RConfigs).</summary>
    public const string ProposerPromptName = "Evaluator.Proposer";

    /// <summary>The knob vocabulary the proposer must choose from (kept in sync with <see cref="Proposal.KnobType"/>).</summary>
    public const string KnobMenu =
        "system-prompt | state-instruction | few-shot | sampling | guardrail | state-graph | model | tool-gating";

    private readonly IInferService _infer = infer;

    /// <inheritdoc/>
    public async Task<Proposal?> ProposeAsync(
        string agentName,
        string currentDefinition,
        SuiteAggregate scores,
        IReadOnlyList<ScoreCard> cards,
        CancellationToken ct = default)
    {
        List<Input> inputs =
        [
            new("Agent Name", agentName),
            new("Current Definition", currentDefinition),
            new("Aggregate Scores", RenderAggregateScores(scores)),
            new("Failing Invariants", RenderFailingInvariants(cards)),
            new("Quality Weaknesses", RenderQualityWeaknesses(cards)),
            new("Knob Menu", KnobMenu)
        ];

        ProposerResponse? resp = await _infer.ToObject<ProposerResponse>(ProposerPromptName, inputs, token: ct);

        string? revised = resp?.RevisedDefinition;
        if (string.IsNullOrWhiteSpace(revised) || revised == currentDefinition)
            return null; // no useful change

        string diff = UnifiedDiff(currentDefinition, revised);
        return new Proposal(
            resp!.KnobType ?? string.Empty,
            revised,
            diff,
            resp.Rationale ?? string.Empty);
    }

    private static string RenderAggregateScores(SuiteAggregate s)
    {
        StringBuilder sb = new();
        sb.Append($"invariant_pass_rate: {s.InvariantPassRate:0.00}; ");
        sb.Append($"quality_mean: {s.QualityMean:0.0}; ");
        sb.Append($"quality_p10: {s.QualityP10:0.0}; ");
        sb.Append($"cost_mean_usd: {s.CostMean:0.0000}; ");
        sb.Append($"latency_p90_ms: {s.LatencyP90Ms}; ");
        sb.Append($"runs: {s.RunCount} (gated: {s.GatedRunCount})");

        if (s.InvariantPassRateById.Count > 0)
        {
            string perInv = string.Join(", ",
                s.InvariantPassRateById.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:0.00}"));
            sb.Append("\nper-invariant pass-rate: ").Append(perInv);
        }

        return sb.ToString();
    }

    private static string RenderFailingInvariants(IReadOnlyList<ScoreCard> cards)
    {
        var groups = cards
            .SelectMany(c => c.Invariants)
            .Where(i => !i.Passed)
            .GroupBy(i => i.Id)
            .OrderBy(g => g.Key)
            .ToList();

        if (groups.Count == 0) return "(no failing invariants)";

        return string.Join("\n", groups.Select(g =>
        {
            string evidence = g.Select(i => i.Evidence)
                .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "(no evidence captured)";
            return $"- {g.Key} ({g.Count()} failure(s)): {evidence}";
        }));
    }

    private static string RenderQualityWeaknesses(IReadOnlyList<ScoreCard> cards)
    {
        List<string> lines = [];

        foreach (string rationale in cards
                     .Select(c => c.Quality?.Rationale)
                     .Where(r => !string.IsNullOrWhiteSpace(r))
                     .Select(r => r!)
                     .Distinct())
        {
            lines.Add($"- {rationale}");
        }

        // Surface the lowest-scoring facet rationales — those are the concrete weaknesses to fix.
        foreach (FacetScore facet in cards
                     .Where(c => c.Quality is not null)
                     .SelectMany(c => c.Quality!.Facets)
                     .Where(f => !string.IsNullOrWhiteSpace(f.Rationale))
                     .OrderBy(f => f.Score)
                     .Take(8))
        {
            lines.Add($"- [{facet.Name} {facet.Score}/10] {facet.Rationale}");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "(no quality weaknesses recorded)";
    }

    /// <summary>
    /// A small, dependency-free unified-style line diff of <paramref name="oldText"/> vs
    /// <paramref name="newText"/>. Uses an LCS to mark unchanged context lines and emits added/removed
    /// lines with <c>+</c>/<c>-</c> prefixes. Intended for human review, not for machine re-application.
    /// </summary>
    internal static string UnifiedDiff(string oldText, string newText)
    {
        string[] a = SplitLines(oldText);
        string[] b = SplitLines(newText);

        // LCS table over lines.
        int n = a.Length, m = b.Length;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        StringBuilder sb = new();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                sb.Append("  ").Append(a[x]).Append('\n');
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                sb.Append("- ").Append(a[x]).Append('\n');
                x++;
            }
            else
            {
                sb.Append("+ ").Append(b[y]).Append('\n');
                y++;
            }
        }
        while (x < n) sb.Append("- ").Append(a[x++]).Append('\n');
        while (y < m) sb.Append("+ ").Append(b[y++]).Append('\n');

        return sb.ToString();
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>DTO matching the <c>Evaluator.Proposer</c> prompt's JSON output.</summary>
    private sealed class ProposerResponse
    {
        [JsonProperty("knob_type")] public string? KnobType { get; set; }
        [JsonProperty("rationale")] public string? Rationale { get; set; }
        [JsonProperty("revised_definition")] public string? RevisedDefinition { get; set; }
        [JsonProperty("expected_impact")] public string? ExpectedImpact { get; set; }
    }
}
