// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// Contract tests for the embedded Evaluator prompts — the meta-LLM calls the whole refinement loop
/// depends on. They pin three things a live campaign already broke once:
/// 1. Every evaluator prompt declares an explicit <c>max-tokens</c>, so a verdict can never be truncated
///    by a provider/transformer default (the failure that corrupted campaign 1e13eae1).
/// 2. Every few-shot example OUTPUT loads as strict, exact-typed JSON. YAML examples are normally fine
///    (JsonifyExample converts them) but the conversion stringifies scalars ("score": "7"), so these
///    parse-critical prompts author their examples as JSON directly — and this test keeps them that way.
/// 3. The judge's slimmed output contract: quality/weaknesses/confidence only, plus the Ground Truth
///    input, so the judge never spends its budget on fields the consumer throws away.
/// </summary>
public sealed class EvaluatorPromptContractTests
{
    private static Dictionary<string, Dictionary<string, string>> ParseRefineryEmbeddedPrompts()
    {
        Assembly refinery = typeof(LlmJudge).Assembly;
        Dictionary<string, Dictionary<string, string>> byResource = new();
        foreach (string resourceName in refinery.GetManifestResourceNames()
                     .Where(n => n.Contains(".Prompts.") && n.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase)))
        {
            using Stream stream = refinery.GetManifestResourceStream(resourceName)!;
            using StreamReader reader = new(stream);
            byResource[resourceName] = RConfigParser.ReadEmbedded(reader.ReadToEnd());
        }
        return byResource;
    }

    private static Prompt LoadPrompt(string fileName)
    {
        Dictionary<string, string> data = ParseRefineryEmbeddedPrompts()
            .Single(kv => kv.Key.EndsWith("." + fileName, StringComparison.Ordinal)).Value;
        return Prompt.ToObject(data);
    }

    [Theory]
    [InlineData("AgentRunJudge.pmt", 3000)]
    [InlineData("PairwiseJudge.pmt", 2000)]
    [InlineData("Proposer.pmt", 8000)]
    [InlineData("ScenarioGenerator.pmt", 8000)]
    public void Evaluator_prompts_declare_an_explicit_output_budget(string file, int expected)
    {
        LoadPrompt(file).OutputBudget.Should().Be(expected,
            "evaluator outputs are machine-parsed; a truncated verdict loses the whole result, so the " +
            "budget must be declared on the prompt, not inherited from provider defaults");
    }

    [Theory]
    [InlineData("AgentRunJudge.pmt", "claude-sonnet-4-6")]    // volume call (1× per scored run) — cost-pinned
    [InlineData("PairwiseJudge.pmt", "claude-sonnet-4-6")]    // volume call (≤8× per candidate) — cost-pinned
    [InlineData("Proposer.pmt", "claude-opus-4-8")]           // 1× per round — depth-pinned
    [InlineData("ScenarioGenerator.pmt", "claude-opus-4-8")]  // rare authoring call — depth-pinned
    public void Evaluator_prompts_declare_an_explicit_model_fallback_chain(string file, string firstChoice)
    {
        Prompt prompt = LoadPrompt(file);
        prompt.PreferredModels.Should().NotBeNull();
        prompt.PreferredModels.Should().HaveCountGreaterThan(1,
            "a single preferred model that a deployment does not register means a SILENT tier-fallback " +
            "substitution; the fallback order must be an explicit, chosen chain");
        prompt.PreferredModels![0].Should().Be(firstChoice,
            "the model each evaluator runs on is a deliberate cost-vs-depth decision, not an accident");
    }

    [Theory]
    [InlineData("AgentRunJudge.pmt")]
    [InlineData("PairwiseJudge.pmt")]
    [InlineData("Proposer.pmt")]
    [InlineData("ScenarioGenerator.pmt")]
    public void Evaluator_prompt_example_outputs_load_as_strict_json(string file)
    {
        Prompt prompt = LoadPrompt(file);

        prompt.Examples.Should().NotBeNullOrEmpty($"{file} must ship at least one few-shot example");
        foreach (Example example in prompt.Examples!)
        {
            Action parse = () => JsonDocument.Parse(example.Output);
            parse.Should().NotThrow(
                $"every {file} example output must be valid JSON after prompt load — the example is what " +
                "the model imitates, and this contract is the loop's single point of failure");
        }
    }

    [Fact]
    public void Judge_example_outputs_use_exact_scalar_types_and_the_slim_contract()
    {
        Prompt judge = LoadPrompt("AgentRunJudge.pmt");

        foreach (Example example in judge.Examples!)
        {
            using JsonDocument doc = JsonDocument.Parse(example.Output);
            JsonElement root = doc.RootElement;

            // Slim contract: exactly the fields the consumer keeps, quality first.
            root.EnumerateObject().Select(p => p.Name).Should().Equal("quality", "weaknesses", "confidence");

            root.GetProperty("quality").GetProperty("overall_score").ValueKind.Should().Be(JsonValueKind.Number,
                "scores must be demonstrated as JSON numbers, not strings (the YamlDotNet conversion pitfall)");
            root.GetProperty("confidence").ValueKind.Should().Be(JsonValueKind.Number);
            root.GetProperty("weaknesses").ValueKind.Should().Be(JsonValueKind.Array);

            foreach (JsonElement facet in root.GetProperty("quality").GetProperty("facets").EnumerateArray())
                facet.GetProperty("score").ValueKind.Should().Be(JsonValueKind.Number);
        }
    }

    [Fact]
    public void Judge_examples_demonstrate_a_genuine_score_spread()
    {
        Prompt judge = LoadPrompt("AgentRunJudge.pmt");

        // Anti-clustering: at least one example must show facets diverging by 4+ points, licensing the
        // judge to differentiate instead of central-clustering every run at the same middle-high score.
        bool anySpread = judge.Examples!.Any(example =>
        {
            using JsonDocument doc = JsonDocument.Parse(example.Output);
            List<int> scores = doc.RootElement.GetProperty("quality").GetProperty("facets")
                .EnumerateArray().Select(f => f.GetProperty("score").GetInt32()).ToList();
            return scores.Count > 1 && scores.Max() - scores.Min() >= 4;
        });

        anySpread.Should().BeTrue("a uniform-score example teaches the judge to flatten; one example must show real facet divergence");
    }

    [Fact]
    public void Judge_prompt_receives_ground_truth_and_drops_discarded_fields()
    {
        Dictionary<string, string> data = ParseRefineryEmbeddedPrompts()
            .Single(kv => kv.Key.EndsWith(".AgentRunJudge.pmt", StringComparison.Ordinal)).Value;

        string instruction = data["_instruction"];
        instruction.Should().Contain("{Ground-Truth}",
            "curated ground truth exists precisely for factual grading; the judge must see it");
        instruction.Should().NotContain("invariant_findings",
            "invariants are decided deterministically and passed IN — re-deriving them wasted the token budget the score needed");
        instruction.Should().NotContain("\"recommendations\"",
            "improvement proposals are the Proposer's job; judge recommendations were never routed anywhere");
        instruction.Should().NotContain("\"verdict\"",
            "pass/fail is computed from the deterministic invariant results, not the judge");
    }
}
