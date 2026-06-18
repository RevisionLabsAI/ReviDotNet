// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 8 audit fixes (D72/D73 tier casing, D78 web OutputFormat, D79 chunk merge).
/// </summary>
public class Batch8FixesTests
{
    // ── D72/D73: tier strings parse case-insensitively, so a lowercase min-tier routes correctly ──

    [Fact]
    public void Find_LowercaseMinTier_ResolvesToThatTierNotC()
    {
        string suffix = Guid.NewGuid().ToString("n").Substring(0, 8);
        ModelManager.Add(new ModelProfile { Name = $"a-{suffix}", Enabled = true, ModelString = "m", Tier = ModelTier.A });
        ModelManager.Add(new ModelProfile { Name = $"c-{suffix}", Enabled = true, ModelString = "m", Tier = ModelTier.C });

        // Lowercase "a" must mean tier A. Before the fix it silently became C, so a C model would qualify.
        ModelProfile? found = ModelManager.Find("a", needsPromptCompletion: false);

        found.Should().NotBeNull();
        found!.Tier.Should().Be(ModelTier.A);
    }

    // ── D78: WebDocument.Content returns the representation selected by Format ──

    [Theory]
    [InlineData(WebOutputFormat.Markdown, "MD")]
    [InlineData(WebOutputFormat.Html, "<p>HTML</p>")]
    [InlineData(WebOutputFormat.Text, "TXT")]
    public void WebDocument_Content_ReturnsSelectedFormat(WebOutputFormat format, string expected)
    {
        var doc = new WebDocument
        {
            Url = "https://x",
            Markdown = "MD",
            Html = "<p>HTML</p>",
            Text = "TXT",
            Format = format,
            FetchInfo = new WebFetchInfo(),
        };

        doc.Content.Should().Be(expected);
    }

    // ── D79: HeadingTokenChunker merges sub-MinChunkTokens chunks forward ──

    [Fact]
    public void Chunk_SmallSections_AreMergedForward()
    {
        var chunker = new HeadingTokenChunker();
        string markdown = "# A\n\nshort\n\n# B\n\nshort two\n\n# C\n\nshort three";

        var options = new ChunkOptions
        {
            MaxTokens = 400,
            MinChunkTokens = 100,   // far larger than any individual tiny section
            PrependHeadingTrail = false,
        };

        var chunks = chunker.Chunk(markdown, new WebMetadata(), options);

        // All three tiny sections merge forward into a single chunk (well under MaxTokens).
        chunks.Should().HaveCount(1);
    }

    [Fact]
    public void Chunk_NoMerge_WhenMinChunkTokensZero()
    {
        var chunker = new HeadingTokenChunker();
        string markdown = "# A\n\nshort\n\n# B\n\nshort two\n\n# C\n\nshort three";

        var options = new ChunkOptions
        {
            MaxTokens = 400,
            MinChunkTokens = 0,     // merging disabled
            PrependHeadingTrail = false,
        };

        var chunks = chunker.Chunk(markdown, new WebMetadata(), options);

        chunks.Should().HaveCount(3);
    }
}
