// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 7 audit fixes (D61 forge.rcfg key parsing).
/// </summary>
public class Batch7FixesTests
{
    // ── D61: a forge.rcfg parses to the underscore-joined keys ForgeManager.Load now reads ──

    [Fact]
    public void ForgeRcfg_ParsesToUnderscoreJoinedGeneralKeys()
    {
        string content =
            "[[general]]\n" +
            "enabled = true\n" +
            "forge-url = https://forge.example.com\n" +
            "api-key = environment\n" +
            "client-id = my-app\n" +
            "timeout-seconds = 300\n";

        Dictionary<string, string> data = RConfigParser.ReadEmbedded(content);

        // ForgeManager.Load looks these exact keys up; the old dot-keyed lookups never matched.
        data.Should().ContainKey("general_enabled").WhoseValue.Should().Be("true");
        data.Should().ContainKey("general_forge-url").WhoseValue.Should().Be("https://forge.example.com");
        data.Should().ContainKey("general_api-key").WhoseValue.Should().Be("environment");
        data.Should().ContainKey("general_client-id").WhoseValue.Should().Be("my-app");
        data.Should().ContainKey("general_timeout-seconds").WhoseValue.Should().Be("300");

        // The dot-separated form the buggy loader used must NOT exist.
        data.Should().NotContainKey("general.enabled");
    }
}
