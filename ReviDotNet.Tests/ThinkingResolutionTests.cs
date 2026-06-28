// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Deterministic unit tests for the thinking common-vocabulary translation
/// (<see cref="ModelProfile.ResolveThinking"/>): common words map through the per-model
/// <c>thinking-conversion-*</c> table, off/none/empty disable, and raw values pass through.
/// </summary>
public class ThinkingResolutionTests
{
    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData("LOW")]   // case-insensitive
    [InlineData(" high ")] // trimmed
    public void CommonWord_WithoutTable_PassesThroughNormalized(string input)
    {
        ModelProfile model = new() { Name = "m" };
        model.ResolveThinking(input).Should().Be(input.Trim().ToLowerInvariant());
    }

    [Theory]
    [InlineData("off")]
    [InlineData("none")]
    [InlineData("disabled")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DisableSentinels_ReturnNull(string? input)
    {
        ModelProfile model = new() { Name = "m" };
        model.ResolveThinking(input).Should().BeNull();
    }

    [Fact]
    public void ConversionTable_TranslatesFiveWords()
    {
        // Claude-style: `minimal` floors to `low` (no sub-low tier); `max` reaches the ceiling.
        ModelProfile claude = new()
        {
            Name = "claude",
            ThinkingConversionMinimal = "low",
            ThinkingConversionLow = "low",
            ThinkingConversionMedium = "medium",
            ThinkingConversionHigh = "high",
            ThinkingConversionMax = "max",
        };
        claude.ResolveThinking("minimal").Should().Be("low");
        claude.ResolveThinking("high").Should().Be("high");
        claude.ResolveThinking("max").Should().Be("max");

        // Gemini-style: the five words map onto numeric token budgets.
        ModelProfile gemini = new()
        {
            Name = "gemini",
            ThinkingConversionMinimal = "512",
            ThinkingConversionLow = "2048",
            ThinkingConversionMedium = "8192",
            ThinkingConversionHigh = "16384",
            ThinkingConversionMax = "24576",
        };
        gemini.ResolveThinking("minimal").Should().Be("512");
        gemini.ResolveThinking("max").Should().Be("24576");

        // OpenAI-style: `max` caps at `high` (no effort above high), `minimal` is native.
        ModelProfile openai = new()
        {
            Name = "openai",
            ThinkingConversionMinimal = "minimal",
            ThinkingConversionMax = "high",
        };
        openai.ResolveThinking("minimal").Should().Be("minimal");
        openai.ResolveThinking("max").Should().Be("high");
    }

    [Fact]
    public void RawValue_PassesThroughUnchanged()
    {
        ModelProfile model = new() { Name = "m", ThinkingConversionHigh = "max" };
        // A non-common-word value (e.g. an explicit budget or provider effort) is sent verbatim.
        model.ResolveThinking("8192").Should().Be("8192");
        model.ResolveThinking("xhigh").Should().Be("xhigh");
    }
}
