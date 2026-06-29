// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Evaluates a suite's <see cref="SuiteAssertion"/>s against a single output. Contains / NotContains /
/// Regex / JsonPath are pure and deterministic (no inference). ScoreMin is the only kind that uses the
/// optional <see cref="IInferService"/> judge; when no judge is supplied it is skipped and reported as
/// passed with an explanatory note, so deterministic callers and tests never trigger live inference.
/// </summary>
public static class SuiteAssertionEvaluator
{
    /// <summary>The judge prompt used by <see cref="AssertionKind.ScoreMin"/> to grade an output.</summary>
    private const string JudgePrompt = "Optimizer.Analyzer";

    /// <summary>Max characters captured into <see cref="AssertionResult.ActualSnippet"/>.</summary>
    private const int SnippetMax = 200;

    /// <summary>
    /// Evaluates every assertion against <paramref name="output"/>. Each result records pass/fail, a
    /// truncated snippet of the output, and a failure reason when applicable. A null/empty assertion list
    /// yields an empty result list.
    /// </summary>
    public static async Task<IReadOnlyList<AssertionResult>> EvaluateAsync(
        string output,
        IReadOnlyList<SuiteAssertion> assertions,
        IInferService? judge = null,
        CancellationToken ct = default)
    {
        if (assertions is null || assertions.Count == 0)
            return Array.Empty<AssertionResult>();

        output ??= string.Empty;
        var results = new List<AssertionResult>(assertions.Count);

        foreach (SuiteAssertion a in assertions)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(a.Kind switch
            {
                AssertionKind.Contains => EvalContains(a, output, expectPresent: true),
                AssertionKind.NotContains => EvalContains(a, output, expectPresent: false),
                AssertionKind.Regex => EvalRegex(a, output),
                AssertionKind.JsonPath => EvalJsonPath(a, output),
                AssertionKind.ScoreMin => await EvalScoreMinAsync(a, output, judge, ct).ConfigureAwait(false),
                _ => new AssertionResult(a.Id, false, Snippet(output), $"Unknown assertion kind '{a.Kind}'.")
            });
        }

        return results;
    }

    // ── Contains / NotContains ───────────────────────────────────────────

    private static AssertionResult EvalContains(SuiteAssertion a, string output, bool expectPresent)
    {
        bool present = output.Contains(a.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        bool passed = present == expectPresent;
        if (passed) return new AssertionResult(a.Id, true, Snippet(output), null);

        string reason = expectPresent
            ? $"Output does not contain '{a.Target}'."
            : $"Output unexpectedly contains '{a.Target}'.";
        return new AssertionResult(a.Id, false, Snippet(output), reason);
    }

    // ── Regex ────────────────────────────────────────────────────────────

    private static AssertionResult EvalRegex(SuiteAssertion a, string output)
    {
        try
        {
            var match = Regex.Match(output, a.Target ?? string.Empty, RegexOptions.None);
            if (match.Success)
                return new AssertionResult(a.Id, true, Snippet(match.Value), null);
            return new AssertionResult(a.Id, false, Snippet(output), $"Output does not match pattern '{a.Target}'.");
        }
        catch (ArgumentException ex)
        {
            return new AssertionResult(a.Id, false, Snippet(output), $"Invalid regex pattern '{a.Target}': {ex.Message}");
        }
    }

    // ── JsonPath (simple dotted path) ────────────────────────────────────

    private static AssertionResult EvalJsonPath(SuiteAssertion a, string output)
    {
        JToken root;
        try
        {
            root = JToken.Parse(output);
        }
        catch (JsonException ex)
        {
            return new AssertionResult(a.Id, false, Snippet(output), $"Output is not valid JSON: {ex.Message}");
        }

        string path = a.Target ?? string.Empty;
        JToken? current = root;
        foreach (string rawSeg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            string seg = rawSeg.Trim();
            if (current is null)
                break;

            if (current is JArray arr)
            {
                if (!int.TryParse(seg, out int idx) || idx < 0 || idx >= arr.Count)
                    return new AssertionResult(a.Id, false, Snippet(output),
                        $"JSON path segment '{seg}' is not a valid index into an array of length {arr.Count}.");
                current = arr[idx];
            }
            else if (current is JObject obj)
            {
                JToken? next = obj[seg];
                if (next is null)
                    return new AssertionResult(a.Id, false, Snippet(output),
                        $"JSON path segment '{seg}' was not found.");
                current = next;
            }
            else
            {
                return new AssertionResult(a.Id, false, Snippet(output),
                    $"JSON path segment '{seg}' cannot be resolved on a scalar value.");
            }
        }

        if (current is null || current.Type == JTokenType.Null)
            return new AssertionResult(a.Id, false, Snippet(output), $"JSON path '{path}' resolved to null.");

        return new AssertionResult(a.Id, true, Snippet(current.ToString()), null);
    }

    // ── ScoreMin (judge-backed) ──────────────────────────────────────────

    private static async Task<AssertionResult> EvalScoreMinAsync(
        SuiteAssertion a, string output, IInferService? judge, CancellationToken ct)
    {
        if (judge is null)
            return new AssertionResult(a.Id, true, Snippet(output), "judge unavailable — ScoreMin skipped");

        double threshold = a.Threshold ?? 0.0;

        AnalysisResult? assessment;
        try
        {
            var inputs = new List<Input>
            {
                new("Prompt Name", "SuiteAssertion.ScoreMin"),
                new("Model", "judge"),
                new("Inputs", string.IsNullOrWhiteSpace(a.Target) ? "(none)" : a.Target),
                new("Response", output)
            };
            assessment = await judge.ToObject<AnalysisResult>(JudgePrompt, inputs, token: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AssertionResult(a.Id, false, Snippet(output), $"Judge failed: {ex.Message}");
        }

        if (assessment is null)
            return new AssertionResult(a.Id, false, Snippet(output), "Judge returned no assessment.");

        // QualityScore is 1..10; normalise to 0..1 for comparison against the threshold.
        double normalized = Math.Clamp(assessment.QualityScore / 10.0, 0.0, 1.0);
        bool passed = normalized >= threshold;
        return passed
            ? new AssertionResult(a.Id, true, Snippet(output), null)
            : new AssertionResult(a.Id, false, Snippet(output),
                $"Quality score {normalized:0.##} is below threshold {threshold:0.##}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string Snippet(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= SnippetMax ? s : s[..SnippetMax] + "…";
    }
}
