// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 9 audit fixes (D85 lenient ToBool, D86 single-message PromptChatOne).
/// </summary>
public class Batch9FixesTests
{
    // ── D85: ToBool / Util.ParseBool is lenient (trim, punctuation, case, common spellings) ──

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData(" true\n", true)]
    [InlineData("\"Yes.\"", true)]
    [InlineData("y", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("NO", false)]
    [InlineData(" 0 ", false)]
    [InlineData("maybe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseBool_IsLenient(string? input, bool? expected)
    {
        Revi.Util.ParseBool(input).Should().Be(expected);
    }

    // ── D86: PromptChatOne packs few-shot examples into ONE user message; PromptChatMulti keeps them separate ──

    [Fact]
    public void BuildMessages_SingleMessage_PacksExamplesIntoOneUserMessage()
    {
        Prompt prompt = MakePromptWithTwoExamples();
        ModelProfile model = MakeChatModel();

        List<Message> messages = CompletionChat.BuildMessages(prompt, model, inputs: null, singleMessageExamples: true);

        // No separate assistant turns — example outputs are inlined into the single user message.
        messages.Count(m => m.Role == "assistant").Should().Be(0);
        Message user = messages.Should().ContainSingle(m => m.Role == "user").Subject;
        user.Content.Should().Contain("OUT0").And.Contain("OUT1");
    }

    [Fact]
    public void BuildMessages_MultiMessage_KeepsExamplesAsSeparateTurns()
    {
        Prompt prompt = MakePromptWithTwoExamples();
        ModelProfile model = MakeChatModel();

        List<Message> messages = CompletionChat.BuildMessages(prompt, model, inputs: null, singleMessageExamples: false);

        // One assistant turn per example (the existing PromptChatMulti / ChatOnly behavior).
        messages.Count(m => m.Role == "assistant").Should().Be(2);
    }

    private static Prompt MakePromptWithTwoExamples() => new()
    {
        Name = "p",
        Version = 1,
        System = "Sys",
        Instruction = "Do the task.",
        Examples = new List<Example>
        {
            new(new List<Input>(), "OUT0"),
            new(new List<Input>(), "OUT1"),
        },
    };

    private static ModelProfile MakeChatModel() => new()
    {
        DefaultSystemInputType = InputType.None,
        DefaultInstructionInputType = InputType.None,
        SystemMessage = true,
        PromptInSystem = false,
        SystemInUser = true,
        PromptInUser = true,
    };
}
