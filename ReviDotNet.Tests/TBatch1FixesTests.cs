// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 1 design-improvement fixes (T2, T3-residual, T8, T9, T10).
/// (T1, T3, T4, T5, T6, T7 were already delivered by the D-item remediation and are covered there.)
/// </summary>
public class TBatch1FixesTests
{
    // ── T2: a forge.rcfg with underscore-joined keys activates ForgeManager.IsConfigured via Load() ──

    [Fact]
    public void ForgeManager_Load_UnderscoreKeys_ActivatesIsConfigured()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "RConfigs");
        string path = Path.Combine(dir, "forge.rcfg");
        Directory.CreateDirectory(dir);
        string? backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            File.WriteAllText(path,
                "[[general]]\n" +
                "enabled = true\n" +
                "forge-url = https://forge.example/\n" +
                "api-key = test-key\n" +
                "client-id = test-client\n" +
                "timeout-seconds = 120\n");

            ForgeManager.Reset();
            ForgeManager.IsConfigured.Should().BeFalse();

            ForgeManager.Load();

            ForgeManager.IsConfigured.Should().BeTrue();
            ForgeManager.Config!.ForgeUrl.Should().Be("https://forge.example/");
            ForgeManager.Config!.ClientId.Should().Be("test-client");
        }
        finally
        {
            if (backup != null) File.WriteAllText(path, backup);
            else File.Delete(path);
            ForgeManager.Reset();   // never leak active Forge into other tests
        }
    }

    [Fact]
    public void ForgeManager_Load_Disabled_DoesNotActivate()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "RConfigs");
        string path = Path.Combine(dir, "forge.rcfg");
        Directory.CreateDirectory(dir);
        string? backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            File.WriteAllText(path, "[[general]]\nenabled = false\nforge-url = https://forge.example/\n");
            ForgeManager.Reset();
            ForgeManager.Load();
            ForgeManager.IsConfigured.Should().BeFalse();
        }
        finally
        {
            if (backup != null) File.WriteAllText(path, backup);
            else File.Delete(path);
            ForgeManager.Reset();
        }
    }

    // ── T3 residual: IsCompletion()/IsChat() recognize the documented kebab completion-type forms ──

    [Theory]
    [InlineData("prompt-only", true)]
    [InlineData("prompt-chat-one", true)]
    [InlineData("prompt-chat-multi", true)]
    [InlineData("PromptOnly", true)]
    [InlineData("completion", true)]   // legacy alias preserved
    [InlineData("chat-only", false)]
    [InlineData("chat", false)]        // legacy alias preserved
    [InlineData("auto", false)]
    [InlineData(null, false)]
    public void IsCompletion_RecognizesKebabAndLegacyForms(string? completionType, bool expected)
    {
        Prompt.IsCompletion(completionType).Should().Be(expected);
    }

    [Theory]
    [InlineData("chat-only", true)]
    [InlineData("chat", true)]         // legacy alias preserved
    [InlineData("prompt-chat-one", true)]
    [InlineData("prompt-chat-multi", true)]
    [InlineData("prompt-only", false)]
    [InlineData("completion", false)]
    [InlineData("auto", false)]
    [InlineData(null, false)]
    public void IsChat_RecognizesKebabAndLegacyForms(string? completionType, bool expected)
    {
        Prompt.IsChat(completionType).Should().Be(expected);
    }

    // ── T8: runtime warning / strict-inputs on unfilled placeholders and dropped inputs ──

    [Fact]
    public void BuildMessages_StrictInputs_ThrowsOnUnfilledPlaceholder()
    {
        Prompt prompt = MakeFilledPrompt("Hello {Name}", "Do the task.", strict: true);

        // Provide an input that matches no placeholder, so {Name} is left unfilled AND 'City' is dropped.
        Action act = () => CompletionChat.BuildMessages(prompt, MakeFilledModel(),
            new List<Input> { new("City", "NYC") });

        act.Should().Throw<Exception>().WithMessage("*Strict input validation failed*");
    }

    [Fact]
    public void BuildMessages_WarnOnly_DoesNotThrowOnUnfilledPlaceholder()
    {
        Prompt prompt = MakeFilledPrompt("Hello {Name}", "Do the task.", strict: false);

        Action act = () => CompletionChat.BuildMessages(prompt, MakeFilledModel(),
            new List<Input> { new("City", "NYC") });

        act.Should().NotThrow();   // null/false strict-inputs warns only
    }

    [Fact]
    public void BuildMessages_StrictInputs_DoesNotThrowWhenAllFilled()
    {
        Prompt prompt = MakeFilledPrompt("Hello {Name}", "Do the task.", strict: true);

        Action act = () => CompletionChat.BuildMessages(prompt, MakeFilledModel(),
            new List<Input> { new("Name", "Ada") });

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildMessages_NoInputs_DoesNotValidate()
    {
        // The early-out for no inputs must not trip validation (back-compat for input-less prompts).
        Prompt prompt = MakeFilledPrompt("Hello {Name}", "Do the task.", strict: true);

        Action act = () => CompletionChat.BuildMessages(prompt, MakeFilledModel(), inputs: null);

        act.Should().NotThrow();
    }

    // ── T9: blank lines inside raw [[_...]] sections are preserved (not stripped by the reader) ──

    [Fact]
    public void ReadEmbedded_PreservesBlankLinesInsideRawSection()
    {
        string content =
            "[[information]]\n" +
            "name = p\n" +
            "version = 1\n" +
            "\n" +
            "[[_system]]\n" +
            "Para 1\n" +
            "\n" +
            "Para 2\n";

        Dictionary<string, string> data = RConfigParser.ReadEmbedded(content);

        Normalize(data["_system"]).Should().Be("Para 1\n\nPara 2");
        // Non-raw keys still parse with the underscore join, blanks ignored there.
        data["information_name"].Should().Be("p");
    }

    [Fact]
    public void ReadEmbedded_PreservesBlankLinesInExampleOutput()
    {
        string content =
            "[[information]]\n" +
            "name = p\n" +
            "[[_exout_1]]\n" +
            "line a\n" +
            "\n" +
            "line b\n";

        Dictionary<string, string> data = RConfigParser.ReadEmbedded(content);

        Normalize(data["_exout_1"]).Should().Be("line a\n\nline b");
    }

    // ── T10: unpaired few-shot examples are detected (runtime side) ──

    [Fact]
    public void FindUnpairedExamples_DetectsMissingOutput()
    {
        var data = new Dictionary<string, string>
        {
            ["_exin_1"] = "a", ["_exout_1"] = "b",
            ["_exin_2"] = "c",                       // no _exout_2
        };

        Prompt.FindUnpairedExamples(data).Should().ContainSingle()
            .Which.Should().Be((2, "output"));
    }

    [Fact]
    public void FindUnpairedExamples_DetectsMissingInput()
    {
        var data = new Dictionary<string, string>
        {
            ["_exin_1"] = "a", ["_exout_1"] = "b",
            ["_exout_3"] = "d",                      // no _exin_3
        };

        Prompt.FindUnpairedExamples(data).Should().ContainSingle()
            .Which.Should().Be((3, "input"));
    }

    [Fact]
    public void FindUnpairedExamples_AllPaired_ReturnsEmpty()
    {
        var data = new Dictionary<string, string>
        {
            ["_exin_1"] = "a", ["_exout_1"] = "b",
            ["_exin_2"] = "c", ["_exout_2"] = "d",
        };

        Prompt.FindUnpairedExamples(data).Should().BeEmpty();
    }

    [Fact]
    public void PromptToObject_UnpairedExample_IsDroppedButPairedSurvive()
    {
        var data = new Dictionary<string, string>
        {
            ["information_name"] = "p",
            ["information_version"] = "1",
            ["_system"] = "You are helpful.",
            ["_exin_1"] = "[Q]\nhi", ["_exout_1"] = "hello",
            ["_exin_2"] = "[Q]\norphan",            // no _exout_2 → dropped
        };

        Prompt prompt = Prompt.ToObject(data);
        prompt.Examples.Should().HaveCount(1);      // only the complete pair survives
    }

    // ── Helpers ──

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static Prompt MakeFilledPrompt(string system, string instruction, bool strict) => new()
    {
        Name = "p",
        Version = 1,
        System = system,
        Instruction = instruction,
        StrictInputs = strict,
    };

    private static ModelProfile MakeFilledModel() => new()
    {
        DefaultSystemInputType = InputType.Filled,
        DefaultInstructionInputType = InputType.Filled,
        SystemMessage = true,
        PromptInSystem = false,
        SystemInUser = true,
        PromptInUser = true,
    };
}
