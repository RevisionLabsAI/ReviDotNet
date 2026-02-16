// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================

using System.Collections.Generic;
using Revi;
using Xunit;
using FluentAssertions;

namespace ReviDotNet.Tests;

/// <summary>
/// Tests for the RConfigParser utility, focusing on comment support and basic parsing.
/// </summary>
public class RConfigParserTests
{
    /// <summary>
    /// Verifies that full-line comments starting with '#' are ignored in non-raw sections.
    /// </summary>
    [Fact]
    public void ReadEmbedded_FullLineComment_IsIgnored()
    {
        // Arrange
        string content = """
            [[general]]
            # This is a comment
            name = test-config
            """;

        // Act
        Dictionary<string, string> result = RConfigParser.ReadEmbedded(content);

        // Assert
        result.Should().ContainKey("general_name");
        result["general_name"].Should().Be("test-config");
        result.Count.Should().Be(1);
    }

    /// <summary>
    /// Verifies that inline '#' characters are NOT treated as comments and are preserved in the value.
    /// </summary>
    [Fact]
    public void ReadEmbedded_InlineHash_IsPreserved()
    {
        // Arrange
        string content = """
            [[general]]
            name = test-config # with hash
            """;

        // Act
        Dictionary<string, string> result = RConfigParser.ReadEmbedded(content);

        // Assert
        result.Should().ContainKey("general_name");
        result["general_name"].Should().Be("test-config # with hash");
    }

    /// <summary>
    /// Verifies that '#' after section headers is NOT treated as a comment.
    /// In this case, it will likely cause the header to be ignored because it doesn't end with "]]".
    /// </summary>
    [Fact]
    public void ReadEmbedded_HashAfterSectionHeader_IsNOTComment()
    {
        // Arrange
        string content = """
            [[general]] # Section comment
            name = test-config
            """;

        // Act
        Dictionary<string, string> result = RConfigParser.ReadEmbedded(content);

        // Assert
        // The header "[[general]] # Section comment" does not end with "]]" after trimming,
        // so it won't be recognized as a section header. 
        // Since currentSection starts as "", the key will be "_name".
        result.Should().ContainKey("_name");
        result["_name"].Should().Be("test-config");
    }

    /// <summary>
    /// Verifies that '#' characters inside raw sections (starting with '_') are preserved.
    /// </summary>
    [Fact]
    public void ReadEmbedded_CommentCharInRawSection_IsPreserved()
    {
        // Arrange
        string content = """
            [[_system]]
            You are a helpful assistant. # This should not be a comment
            # This line should be preserved too.
            """;

        // Act
        Dictionary<string, string> result = RConfigParser.ReadEmbedded(content);

        // Assert
        result.Should().ContainKey("_system");
        result["_system"].Should().Contain("# This should not be a comment");
        result["_system"].Should().Contain("# This line should be preserved too.");
    }

    /// <summary>
    /// Verifies that multiple '#' characters on the same line are NOT stripped if not at start.
    /// </summary>
    [Fact]
    public void ReadEmbedded_MultipleHashChars_ArePreserved()
    {
        // Arrange
        string content = """
            [[general]]
            name = test # first # second
            """;

        // Act
        Dictionary<string, string> result = RConfigParser.ReadEmbedded(content);

        // Assert
        result.Should().ContainKey("general_name");
        result["general_name"].Should().Be("test # first # second");
    }

    /// <summary>
    /// Verifies that lines that become empty after stripping comments are ignored.
    /// </summary>
    [Fact]
    public void ReadEmbedded_EmptyLineAfterComment_IsIgnored()
    {
        // Arrange
        string content = """
            [[general]]
            name = test
                # only spaces before hash
            version = 2
            """;

        // Act
        Dictionary<string, string> result = RConfigParser.ReadEmbedded(content);

        // Assert
        result.Should().ContainKey("general_name");
        result.Should().ContainKey("general_version");
        result.Count.Should().Be(2);
    }
}
