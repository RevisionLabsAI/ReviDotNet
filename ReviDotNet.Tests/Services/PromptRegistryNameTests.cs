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
using FluentAssertions;
using Revi;
using Revi.Refinery;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// Regression tests for prompt-registry naming. Prompts must register under their declared
/// <c>[[information]] name</c> verbatim: the loaders used to prefix names with a lowercased
/// subfolder path (e.g. <c>evaluator/Evaluator.AgentRunJudge</c>), which made every subfoldered
/// prompt unreachable because all lookups — <c>LlmJudge</c>, <c>LlmDiffProposer</c>,
/// <c>ScenarioGenerator</c>, Forge's AgentWorkshop/Optimizer services — use the declared name.
/// </summary>
public sealed class PromptRegistryNameTests
{
    // ── On-disk loading: subfolders must not alter the registered name ────────────────

    [Fact]
    public void LoadDirectory_registers_subfoldered_prompt_under_declared_name()
    {
        string root = Path.Combine(Path.GetTempPath(), "revi-prompt-name-test-" + Guid.NewGuid().ToString("N"));
        string promptDir = Path.Combine(root, "Prompts", "Evaluator");
        Directory.CreateDirectory(promptDir);
        try
        {
            File.WriteAllText(Path.Combine(promptDir, "TestPrompt.pmt"),
                "[[information]]\n" +
                "name = Evaluator.TestPrompt\n" +
                "version = 1\n" +
                "\n" +
                "[[_system]]\n" +
                "You are a test prompt.\n");

            PromptManagerService prompts = new(new RecordingReviLogger<PromptManagerService>());
            prompts.LoadDirectory(root);

            prompts.Get("Evaluator.TestPrompt").Should().NotBeNull(
                "a subfoldered prompt must resolve by its declared name, not a folder-prefixed one");
            prompts.GetAll().Should().ContainSingle()
                .Which.Name.Should().Be("Evaluator.TestPrompt",
                    "the registered name must be the declared name verbatim (no 'evaluator/' prefix)");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Embedded Refinery evaluator prompts: names match the code's lookup constants ──

    /// <summary>Parses every embedded .Prompts. resource in the Refinery assembly.</summary>
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

    [Fact]
    public void Refinery_evaluator_prompts_declare_the_names_the_code_looks_up()
    {
        Dictionary<string, Dictionary<string, string>> parsed = ParseRefineryEmbeddedPrompts();
        string[] declaredNames = parsed.Values
            .Select(dict => Prompt.ToObject(dict).Name)
            .Where(n => n != null)
            .Select(n => n!)
            .ToArray();

        declaredNames.Should().Contain(LlmJudge.JudgePromptName);
        declaredNames.Should().Contain(LlmDiffProposer.ProposerPromptName);
        declaredNames.Should().Contain(ScenarioGenerator.GeneratorPromptName);
        declaredNames.Should().Contain("Evaluator.PairwiseJudge");
    }

    [Fact]
    public void Proposer_prompt_survives_its_embedded_agent_file_example()
    {
        // Proposer.pmt's few-shot example input contains a literal .agent file. Its [[information]] /
        // [[_system]] lines must be escaped (\[[...]]) so the parser does not clobber the prompt's own
        // name ("filer") and system section. This is the regression that registered "evaluator/filer".
        Dictionary<string, Dictionary<string, string>> parsed = ParseRefineryEmbeddedPrompts();
        Dictionary<string, string> proposer = parsed
            .Single(kv => kv.Key.EndsWith(".Proposer.pmt", StringComparison.Ordinal)).Value;

        Prompt prompt = Prompt.ToObject(proposer);
        prompt.Name.Should().Be(LlmDiffProposer.ProposerPromptName,
            "the embedded example's 'name = filer' must not clobber the prompt's declared name");

        proposer.Should().ContainKey("_exin_1");
        proposer["_exin_1"].Should().Contain("[[information]]",
            "the escape backslash is stripped on read, leaving the literal section header in the example");
        proposer["_exin_1"].Should().Contain("name = filer",
            "the example agent definition must remain intact inside the example input");
    }
}
