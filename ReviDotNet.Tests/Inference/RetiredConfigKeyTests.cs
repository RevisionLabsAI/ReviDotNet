// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Inference;

/// <summary>
/// The 2026-07 rename (token-limit → context-window, max-output-tokens → output-capacity,
/// max-tokens → output-budget) is deliberately BREAKING: unknown keys are normally ignored
/// silently, which would turn a stale config into "no guard, provider fallback" with no signal.
/// A retired key must therefore fail the load loudly, naming its replacement.
/// </summary>
public class RetiredConfigKeyTests
{
    [Theory]
    [InlineData("settings_token-limit", "context-window")]
    [InlineData("settings_max-output-tokens", "output-capacity")]
    [InlineData("settings_max-tokens", "output-budget")]
    [InlineData("override-settings_max-tokens", "output-budget")]
    public void Retired_keys_fail_the_load_naming_the_replacement(string retired, string replacement)
    {
        var data = new Dictionary<string, string> { [retired] = "1000" };

        Action act = () => RConfigParser.ToObject<ModelProfile>(data);

        act.Should().Throw<FormatException>().Which.Message.Should().Contain(replacement);
    }

    [Fact]
    public void Prompt_loader_rejects_retired_keys_too()
    {
        var data = new Dictionary<string, string>
        {
            ["information_name"] = "p",
            ["_system"] = "sys",
            ["settings_max-tokens"] = "3000"
        };

        Action act = () => Prompt.ToObject(data);

        act.Should().Throw<FormatException>().Which.Message.Should().Contain("output-budget");
    }

    [Fact]
    public void New_keys_load_cleanly()
    {
        var model = RConfigParser.ToObject<ModelProfile>(new Dictionary<string, string>
        {
            ["general_name"] = "m",
            ["settings_context-window"] = "200000",
            ["settings_output-capacity"] = "64000",
            ["override-settings_output-budget"] = "8192"
        });

        model!.ContextWindow.Should().Be(200_000);
        model.OutputCapacity.Should().Be(64_000);
        model.OutputBudget.Should().Be("8192");
    }
}
