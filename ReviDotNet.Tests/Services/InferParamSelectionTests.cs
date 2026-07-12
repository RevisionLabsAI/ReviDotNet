// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// Regression tests for <c>InferService.SelectParam&lt;T&gt;</c>: a model-profile override string wins
/// when set, "disabled" turns the parameter off, and — the regression — a prompt that leaves the
/// parameter unset while the model supplies an override must resolve to the parsed override rather
/// than throw. The old object-typed implementation switched on the prompt value's runtime type, so a
/// null prompt value hit <c>promptObj.GetType()</c> and threw an NRE (this is exactly the
/// Refinery-judge case: Evaluator.AgentRunJudge sets no max-tokens while the model profile does),
/// and bool parameters always threw. SelectParam is private, so the tests bind it via reflection.
/// </summary>
public sealed class InferParamSelectionTests
{
    private static T? Invoke<T>(string? modelString, T? promptValue) where T : struct
    {
        MethodInfo generic = typeof(InferService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "SelectParam" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(T));
        try
        {
            return (T?)generic.Invoke(null, [modelString, promptValue]);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    [Fact]
    public void Model_override_wins_even_when_prompt_leaves_parameter_unset()
    {
        // The regression: model has max-tokens = 4096, prompt (e.g. Evaluator.AgentRunJudge) sets none.
        Invoke<int>("4096", null).Should().Be(4096);
        Invoke<float>("0.7", null).Should().Be(0.7f);
        Invoke<bool>("true", null).Should().Be(true);
    }

    [Fact]
    public void Model_override_wins_over_a_set_prompt_value()
    {
        Invoke<int>("32000", 4096).Should().Be(32000, "the model rcfg [[override-*]] sections take precedence");
        Invoke<float>("1", 0.4f).Should().Be(1f);
    }

    [Fact]
    public void Disabled_turns_the_parameter_off_regardless_of_prompt_value()
    {
        Invoke<float>("disabled", 0.5f).Should().BeNull();
        Invoke<float>("DISABLED", 0.5f).Should().BeNull("the sentinel is case-insensitive");
    }

    [Fact]
    public void No_model_override_falls_through_to_the_prompt_value()
    {
        Invoke<float>(null, 0.4f).Should().Be(0.4f);
        Invoke<int>(null, null).Should().BeNull();
    }

    [Fact]
    public void Numeric_parsing_is_invariant_culture()
    {
        // "0.5" must parse as one-half on comma-decimal locales too.
        Invoke<float>("0.5", null).Should().Be(0.5f);
    }
}
