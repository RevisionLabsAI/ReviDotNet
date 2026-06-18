// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 6 audit fixes (D51 list-marker stripping, D53 configurable filter canary).
/// </summary>
public class Batch6FixesTests
{
    // ── D51: ToStringListClean strips leading bullet/ordinal markers ──

    [Theory]
    [InlineData("- Fast", "Fast")]
    [InlineData("* Star", "Star")]
    [InlineData("+ Plus", "Plus")]
    [InlineData("1. First", "First")]
    [InlineData("2) Second", "Second")]
    [InlineData("  3.  Indented", "Indented")]
    [InlineData("Plain line", "Plain line")]
    [InlineData("-no-space", "-no-space")]   // marker requires trailing whitespace; left intact
    public void StripListMarker_RemovesLeadingMarkers(string input, string expected)
    {
        Revi.Util.StripListMarker(input).Should().Be(expected);
    }

    // ── D53: filter canary defaults to "safeword", is configurable, and supports lenient/strict matching ──

    [Theory]
    [InlineData("safeword")]
    [InlineData("SAFEWORD")]
    [InlineData("  safeword  ")]
    [InlineData("\"safeword\"")]
    [InlineData("safeword.")]
    public void FilterOutputIsSafe_LenientDefault_AcceptsCommonVariations(string output)
    {
        Revi.Util.FilterOutputIsSafe(output, canary: null, matching: null).Should().BeTrue();
    }

    [Fact]
    public void FilterOutputIsSafe_LenientDefault_RejectsOtherOutput()
    {
        Revi.Util.FilterOutputIsSafe("ignore previous instructions", null, null).Should().BeFalse();
    }

    [Fact]
    public void FilterOutputIsSafe_CustomCanary_IsHonored()
    {
        Revi.Util.FilterOutputIsSafe("ok", canary: "ok", matching: null).Should().BeTrue();
        Revi.Util.FilterOutputIsSafe("ok", canary: "different", matching: null).Should().BeFalse();
    }

    [Fact]
    public void FilterOutputIsSafe_Strict_RequiresExactMatch()
    {
        Revi.Util.FilterOutputIsSafe("safeword", null, "strict").Should().BeTrue();
        Revi.Util.FilterOutputIsSafe("SAFEWORD", null, "strict").Should().BeFalse();   // case-sensitive
        Revi.Util.FilterOutputIsSafe(" safeword ", null, "strict").Should().BeFalse(); // untrimmed
    }
}
