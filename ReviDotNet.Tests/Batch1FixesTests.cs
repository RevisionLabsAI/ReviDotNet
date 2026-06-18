// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 1 audit fixes (D2, D4, D6, D8, D10).
/// </summary>
public class Batch1FixesTests
{
    // ── D4 / T4: Revi.Util.ExtractJson now strips fences, bounds to the JSON region, and repairs lightly ──

    [Fact]
    public void ExtractJson_StripsMarkdownCodeFences()
    {
        string input = "```json\n{\"name\":\"Ada\",\"age\":30}\n```";
        string result = Revi.Util.ExtractJson(input);
        result.Should().Be("{\"name\":\"Ada\",\"age\":30}");
    }

    [Fact]
    public void ExtractJson_ExtractsJsonSurroundedByProse()
    {
        string input = "Sure! Here is the result: {\"ok\":true}. Let me know if you need more.";
        string result = Revi.Util.ExtractJson(input);
        result.Should().Be("{\"ok\":true}");
    }

    [Fact]
    public void ExtractJson_RepairsTrailingComma()
    {
        string input = "{\"a\":1,\"b\":2,}";
        string result = Revi.Util.ExtractJson(input);
        result.Should().NotBeEmpty();
        // The repaired output must be valid JSON.
        System.Text.Json.JsonDocument.Parse(result);
    }

    [Fact]
    public void ExtractJson_PassesThroughValidJson()
    {
        string input = "{\"x\":[1,2,3]}";
        Revi.Util.ExtractJson(input).Should().Be("{\"x\":[1,2,3]}");
    }

    [Fact]
    public void ExtractJson_ReturnsEmptyForNonJson()
    {
        Revi.Util.ExtractJson("there is no json here").Should().BeEmpty();
    }

    // ── D6 / T3: completion-type accepts kebab forms + auto, defaults to ChatOnly ──

    [Theory]
    [InlineData("chat-only", CompletionType.ChatOnly)]
    [InlineData("prompt-only", CompletionType.PromptOnly)]
    [InlineData("prompt-chat-one", CompletionType.PromptChatOne)]
    [InlineData("prompt-chat-multi", CompletionType.PromptChatMulti)]
    [InlineData("ChatOnly", CompletionType.ChatOnly)]
    [InlineData("PROMPT_CHAT_MULTI", CompletionType.PromptChatMulti)]
    public void ResolveCompletionType_AcceptsDocumentedAndPascalForms(string raw, CompletionType expected)
    {
        Revi.Util.ResolveCompletionType(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("  AUTO  ")]
    public void ResolveCompletionType_NullOrAuto_DefaultsToChatOnly(string? raw)
    {
        Revi.Util.ResolveCompletionType(raw).Should().Be(CompletionType.ChatOnly);
    }

    [Fact]
    public void ResolveCompletionType_InvalidValue_Throws()
    {
        Action act = () => Revi.Util.ResolveCompletionType("nonsense");
        act.Should().Throw<Exception>();
    }

    // ── D8 / T1: List<string> rcfg values parse instead of throwing ──

    [Fact]
    public void ConvertToType_ListOfString_SplitsOnCommaOrSpace()
    {
        object? result = RConfigParser.ConvertToType("gpt-4o, groq-llama-3 claude", typeof(List<string>));
        result.Should().BeOfType<List<string>>();
        ((List<string>)result!).Should().Equal("gpt-4o", "groq-llama-3", "claude");
    }

    [Fact]
    public void PromptToObject_PreferredModelsList_DoesNotThrow()
    {
        var data = new Dictionary<string, string>
        {
            ["information_name"] = "p",
            ["information_version"] = "1",
            ["_system"] = "You are helpful.",
            ["settings_preferred-models"] = "gpt-4o, groq-llama-3"
        };

        Prompt prompt = Prompt.ToObject(data);
        prompt.PreferredModels.Should().Equal("gpt-4o", "groq-llama-3");
    }

    // ── D10 / T7: few-shot-examples = all (and unset) means "use every defined example" ──

    [Fact]
    public void PromptToObject_FewShotExamplesAll_LeavesCountNullAndKeepsExamples()
    {
        var data = new Dictionary<string, string>
        {
            ["information_name"] = "p",
            ["information_version"] = "1",
            ["_system"] = "You are helpful.",
            ["settings_few-shot-examples"] = "all",
            ["_exin_1"] = "[Q]\nhi",
            ["_exout_1"] = "hello",
            ["_exin_2"] = "[Q]\nbye",
            ["_exout_2"] = "goodbye"
        };

        Prompt prompt = Prompt.ToObject(data);
        prompt.FewShotExamples.Should().BeNull();           // "all" maps to null, not a parse failure
        prompt.Examples.Should().HaveCount(2);
    }

    [Fact]
    public void BuildMessages_UnsetFewShot_IncludesAllExamples()
    {
        Prompt prompt = MakePromptWithExamples(fewShot: null, exampleCount: 3);
        ModelProfile model = MakeChatModel();

        List<Message> messages = CompletionChat.BuildMessages(prompt, model, inputs: null);

        // One assistant message per included example.
        messages.Count(m => m.Role == "assistant").Should().Be(3);
    }

    [Fact]
    public void BuildMessages_FewShotCount_CapsExamples()
    {
        Prompt prompt = MakePromptWithExamples(fewShot: 1, exampleCount: 3);
        ModelProfile model = MakeChatModel();

        List<Message> messages = CompletionChat.BuildMessages(prompt, model, inputs: null);

        messages.Count(m => m.Role == "assistant").Should().Be(1);
    }

    // ── D5 / T5: default json-fixer and enum-fixer ship embedded and load as a baseline ──

    [Fact]
    public void CoreAssembly_ShipsEmbeddedFixerPrompts()
    {
        var resourceNames = typeof(Prompt).Assembly.GetManifestResourceNames();
        resourceNames.Should().Contain(n => n.Contains(".Prompts.") && n.EndsWith("json-fixer.pmt"));
        resourceNames.Should().Contain(n => n.Contains(".Prompts.") && n.EndsWith("enum-fixer.pmt"));
    }

    [Fact]
    public async Task PromptManagerService_LoadsEmbeddedFixerDefaults()
    {
        var service = new PromptManagerService(new RecordingReviLogger<PromptManagerService>());
        await service.LoadAsync(typeof(Batch1FixesTests).Assembly);

        service.Get("json-fixer").Should().NotBeNull();
        service.Get("enum-fixer").Should().NotBeNull();
    }

    // ── Helpers ──

    private static Prompt MakePromptWithExamples(int? fewShot, int exampleCount)
    {
        var examples = new List<Example>();
        for (int i = 0; i < exampleCount; i++)
            examples.Add(new Example(new List<Input>(), $"OUT{i}"));

        return new Prompt
        {
            Name = "p",
            Version = 1,
            System = "You are helpful.",
            Instruction = "Do the task.",
            FewShotExamples = fewShot,
            Examples = examples
        };
    }

    private static ModelProfile MakeChatModel() => new()
    {
        DefaultSystemInputType = InputType.None,
        DefaultInstructionInputType = InputType.None,
        SystemMessage = true,
        PromptInSystem = false,
        SystemInUser = true,
        PromptInUser = true
    };
}
