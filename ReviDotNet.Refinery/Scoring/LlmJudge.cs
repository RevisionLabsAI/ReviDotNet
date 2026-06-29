// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;

namespace Revi.Refinery;

/// <summary>Scores the quality of a run via an LLM judge (the high-bar reasoning evaluator prompt).</summary>
public interface ILlmJudge
{
    /// <summary>Judge a run against its scenario rubric; returns null if no judgement could be produced.</summary>
    Task<QualityScore?> JudgeAsync(
        AgentTrace trace,
        string agentDefinition,
        Scenario scenario,
        IReadOnlyList<InvariantResult> invariants,
        CancellationToken ct = default);
}

/// <summary>
/// Drives the <c>Evaluator.AgentRunJudge</c> prompt (pinned to a high-effort reasoning model) to produce a
/// quality assessment grounded in the run's activity log.
/// </summary>
public sealed class LlmJudge(IInferService infer, MetaLlmUsageBroker meta) : ILlmJudge
{
    /// <summary>The judge prompt name (shipped embedded in this assembly's RConfigs).</summary>
    public const string JudgePromptName = "Evaluator.AgentRunJudge";

    private readonly IInferService _infer = infer;
    private readonly MetaLlmUsageBroker _meta = meta;

    /// <inheritdoc/>
    public async Task<QualityScore?> JudgeAsync(
        AgentTrace trace, string agentDefinition, Scenario scenario,
        IReadOnlyList<InvariantResult> invariants, CancellationToken ct = default)
    {
        List<Input> inputs =
        [
            new("Agent Name", trace.AgentName),
            new("Agent Definition", agentDefinition),
            new("Scenario", RenderScenario(scenario)),
            new("Invariants", RenderInvariants(invariants)),
            new("Quality Rubric", scenario.Rubric.Count > 0 ? string.Join("\n", scenario.Rubric.Select(r => "- " + r)) : "(none specified)"),
            new("Activity Log", RenderActivityLog(trace)),
            new("Final Output", trace.FinalOutput ?? string.Empty),
            new("Run Metadata",
                $"exit_reason: {trace.ExitReason}; total_steps: {trace.TotalSteps}; " +
                $"tool_calls: {trace.ToolCalls.Count()}; tokens_in: {trace.InputTokens}; tokens_out: {trace.OutputTokens}")
        ];

        (AgentRunJudgeResponse? resp, CompletionResult? usage) =
            await _infer.ToObjectWithUsage<AgentRunJudgeResponse>(JudgePromptName, inputs, ct: ct);
        _meta.Record(usage);
        if (resp?.Quality is null)
            return null;

        return new QualityScore
        {
            Overall = resp.Quality.OverallScore,
            Facets = (resp.Quality.Facets ?? [])
                .Select(f => new FacetScore(f.Name ?? string.Empty, f.Score, f.Rationale ?? string.Empty))
                .ToList(),
            Rationale = resp.Weaknesses is { Count: > 0 } ? string.Join(" ", resp.Weaknesses) : string.Empty,
            JudgeConfidence = resp.Confidence
        };
    }

    private static string RenderScenario(Scenario s)
    {
        string inputs = s.Inputs.Count > 0
            ? string.Join("\n", s.Inputs.Select(kv => $"[{kv.Key}]\n{kv.Value}"))
            : "(no inputs)";
        return $"{s.Notes}\n\nInputs:\n{inputs}".Trim();
    }

    private static string RenderInvariants(IReadOnlyList<InvariantResult> invariants)
    {
        if (invariants.Count == 0) return "(none)";
        return string.Join("\n", invariants.Select(i =>
            $"- {i.Id} [{(i.Passed ? "passed" : "FAILED")}]: {i.Evidence}"));
    }

    private static string RenderActivityLog(AgentTrace trace)
    {
        IEnumerable<string> lines = trace.Events.Select(e =>
        {
            string o1 = Truncate(e.Object1, 1500);
            string o2 = Truncate(e.Object2, 400);
            string tags = e.State is null ? string.Empty : $" state={e.State} cycle={e.Cycle}";
            string body = string.IsNullOrEmpty(o1) ? string.Empty : $" | {o1}";
            string meta = string.IsNullOrEmpty(o2) ? string.Empty : $" || {o2}";
            return $"[{e.Type}]{tags}{body}{meta}";
        });
        return string.Join("\n", lines);
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length > max ? s[..max] + "…" : s;

    /// <summary>DTO matching the <c>Evaluator.AgentRunJudge</c> prompt's JSON output.</summary>
    private sealed class AgentRunJudgeResponse
    {
        [JsonProperty("verdict")] public string? Verdict { get; set; }
        [JsonProperty("invariant_findings")] public List<InvariantFinding>? InvariantFindings { get; set; }
        [JsonProperty("quality")] public QualityBlock? Quality { get; set; }
        [JsonProperty("strengths")] public List<string>? Strengths { get; set; }
        [JsonProperty("weaknesses")] public List<string>? Weaknesses { get; set; }
        [JsonProperty("recommendations")] public List<Recommendation>? Recommendations { get; set; }
        [JsonProperty("confidence")] public int Confidence { get; set; }

        public sealed class InvariantFinding
        {
            [JsonProperty("id")] public string? Id { get; set; }
            [JsonProperty("passed")] public bool Passed { get; set; }
            [JsonProperty("evidence")] public string? Evidence { get; set; }
        }

        public sealed class QualityBlock
        {
            [JsonProperty("overall_score")] public int OverallScore { get; set; }
            [JsonProperty("facets")] public List<Facet>? Facets { get; set; }
        }

        public sealed class Facet
        {
            [JsonProperty("name")] public string? Name { get; set; }
            [JsonProperty("score")] public int Score { get; set; }
            [JsonProperty("rationale")] public string? Rationale { get; set; }
        }

        public sealed class Recommendation
        {
            [JsonProperty("title")] public string? Title { get; set; }
            [JsonProperty("knob")] public string? Knob { get; set; }
            [JsonProperty("rationale")] public string? Rationale { get; set; }
            [JsonProperty("expected_impact")] public string? ExpectedImpact { get; set; }
        }
    }
}
