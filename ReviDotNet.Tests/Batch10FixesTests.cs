// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Globalization;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 10 audit fixes (D99 culture-invariant numeric parsing).
/// </summary>
public class Batch10FixesTests
{
    // ── D99: numeric config values (e.g. cost-budget) parse invariant, not under the host culture ──

    [Fact]
    public void ConvertToType_Decimal_ParsesInvariant_OnCommaDecimalCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // German uses ',' as the decimal separator and '.' as a thousands group separator.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            object? result = RConfigParser.ConvertToType("0.005", typeof(decimal));

            // Must be the dotted value, not 5 (which is what current-culture parsing would yield).
            result.Should().Be(0.005m);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ConvertToType_Float_ParsesInvariant_OnCommaDecimalCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            object? result = RConfigParser.ConvertToType("1.5", typeof(float));

            result.Should().Be(1.5f);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
