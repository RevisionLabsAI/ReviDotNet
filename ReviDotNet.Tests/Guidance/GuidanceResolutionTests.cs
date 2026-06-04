// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Guidance;

/// <summary>
/// Covers provider <c>default-guidance-type</c> parsing and <see cref="GuidanceResolver"/>.
/// Regression guard for the embedded-provider load failure where <c>json-auto</c> could not be
/// parsed into the provider's default guidance type, and coverage for auto vs manual JSON support.
/// </summary>
public class GuidanceResolutionTests
{
    private sealed class SampleSchema
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private static ProviderProfile ParseProvider(string guidanceType) =>
        RConfigParser.ToObject<ProviderProfile>(
            RConfigParser.ReadEmbedded($@"[[general]]
name = p
enabled = true
protocol = OpenAI
api-url = https://example/v1/
default-model = m

[[guidance]]
supports-guidance = true
default-guidance-type = {guidanceType}
"), "")!;

    [Theory]
    [InlineData("json-auto", GuidanceSchemaType.JsonAuto)]
    [InlineData("json-manual", GuidanceSchemaType.JsonManual)]
    [InlineData("regex-auto", GuidanceSchemaType.RegexAuto)]
    [InlineData("Disabled", GuidanceSchemaType.Disabled)]
    public void Provider_DefaultGuidanceType_ParsesSchemaStrategy(string raw, GuidanceSchemaType expected)
    {
        ProviderProfile provider = ParseProvider(raw);
        provider.DefaultGuidanceType.Should().Be(expected);
    }

    [Fact]
    public void Resolver_JsonAuto_GeneratesSchemaFromOutputType()
    {
        GuidanceResolver.Resolve(
            GuidanceSchemaType.JsonAuto,
            manualSchema: null,
            outputType: typeof(SampleSchema),
            chainOfThought: false,
            out GuidanceType? type,
            out string? schema);

        type.Should().Be(GuidanceType.Json);
        schema.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Resolver_JsonManual_UsesProvidedSchema()
    {
        GuidanceResolver.Resolve(
            GuidanceSchemaType.JsonManual,
            manualSchema: "MY_SCHEMA",
            outputType: typeof(SampleSchema),
            chainOfThought: false,
            out GuidanceType? type,
            out string? schema);

        type.Should().Be(GuidanceType.Json);
        schema.Should().Be("MY_SCHEMA");
    }

    [Theory]
    [InlineData(GuidanceSchemaType.JsonAuto, GuidanceType.Json)]
    [InlineData(GuidanceSchemaType.JsonManual, GuidanceType.Json)]
    [InlineData(GuidanceSchemaType.RegexAuto, GuidanceType.Regex)]
    [InlineData(GuidanceSchemaType.RegexManual, GuidanceType.Regex)]
    [InlineData(GuidanceSchemaType.GNBFManual, GuidanceType.Grammar)]
    [InlineData(GuidanceSchemaType.Disabled, GuidanceType.Disabled)]
    public void Reduce_MapsSchemaStrategyToDecodeMode(GuidanceSchemaType schema, GuidanceType expected)
    {
        GuidanceResolver.ReduceToGuidanceType(schema).Should().Be(expected);
    }

    [Theory]
    [InlineData(GuidanceSchemaType.Default)]
    [InlineData(null)]
    public void Reduce_DefaultOrNull_HasNoStandaloneDecodeMode(GuidanceSchemaType? schema)
    {
        GuidanceResolver.ReduceToGuidanceType(schema).Should().BeNull();
    }
}
