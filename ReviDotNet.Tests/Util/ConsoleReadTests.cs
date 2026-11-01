// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.IO;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Util;

/// <summary>Console input is process-wide, so these redirected-input tests run without parallel collections.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleReadCollection
{
    /// <summary>The collection containing redirected-console tests.</summary>
    public const string Name = "Console read contracts";
}

/// <summary>Closed input declines confirmation and cannot trap a required text prompt in an endless loop.</summary>
[Collection(ConsoleReadCollection.Name)]
public sealed class ConsoleReadTests
{
    /// <summary>Explicit answers and submitted defaults differ from a closed input stream.</summary>
    /// <param name="input">The redirected input.</param>
    /// <param name="recommendation">The displayed default.</param>
    /// <param name="expected">Whether the user confirmed the question.</param>
    [Theory]
    [InlineData("", true, false)]
    [InlineData("", false, false)]
    [InlineData("", null, false)]
    [InlineData("\n", true, true)]
    [InlineData("\n", false, false)]
    [InlineData("\nyes\n", null, true)]
    [InlineData(" NO \n", true, false)]
    [InlineData("TRUE\n", false, true)]
    [InlineData("invalid\nn\n", null, false)]
    public void ConfirmationDistinguishesAnswersFromClosedInput(string input, bool? recommendation, bool expected)
    {
        TextReader original = Console.In;
        using StringReader reader = new(input);
        try
        {
            Console.SetIn(reader);
            Revi.Util.ReadYesNo("Continue?", recommendation).Should().Be(expected);
        }
        finally { Console.SetIn(original); }
    }

    /// <summary>Required text fails explicitly at end of input; optional input has a defined empty answer.</summary>
    [Fact]
    public void ClosedTextInputDoesNotLoopForever()
    {
        TextReader original = Console.In;
        using StringReader reader = new(string.Empty);
        try
        {
            Console.SetIn(reader);
            Revi.Util.ReadOpenEnded("Optional").Should().BeEmpty();
            Action required = () => Revi.Util.ReadOpenEnded("Required", required: true);
            required.Should().Throw<EndOfStreamException>();
            Revi.Util.ReadInt("Count", 7).Should().Be(7);
        }
        finally { Console.SetIn(original); }
    }
}
