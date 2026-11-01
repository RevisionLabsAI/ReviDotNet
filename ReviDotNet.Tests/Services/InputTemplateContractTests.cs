// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>Missing listed-input configuration reports a useful error before formatting any input.</summary>
public sealed class InputTemplateContractTests
{
    /// <summary>The selected template must exist; a different template cannot silently substitute for it.</summary>
    /// <param name="multiple">Whether the call supplies multiple inputs.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTemplateReportsTheModelAndTemplate(bool multiple)
    {
        ServiceCollection services = new();
        services.AddReviDotNet();
        using ServiceProvider provider = services.BuildServiceProvider();
        IInferService inference = provider.GetRequiredService<IInferService>();
        ModelProfile model = new() { Name = "template-contract" };
        List<Input> inputs = [new Input("first", "one")];
        if (multiple) inputs.Add(new Input("second", "two"));

        Action format = () => inference.ListInputs(model, inputs);
        format.Should().Throw<InvalidOperationException>()
            .WithMessage($"*template-contract*{(multiple ? "multi-item" : "single-item")}*");
    }

    /// <summary>Initialization failures are retried, and successfully computed null values are cached.</summary>
    [Fact]
    public void RetryableLazyDistinguishesUninitializedFromAnInitializedNull()
    {
        int attempts = 0;
        Revi.Util.LazyWithNoExceptionCaching<string?> lazy = new(() =>
        {
            if (++attempts == 1) throw new InvalidOperationException("retry");
            return null;
        });
        Action first = () => _ = lazy.Value;
        first.Should().Throw<InvalidOperationException>();
        lazy.IsValueCreated.Should().BeFalse();
        lazy.Value.Should().BeNull();
        lazy.Value.Should().BeNull();
        attempts.Should().Be(2);
    }
}
