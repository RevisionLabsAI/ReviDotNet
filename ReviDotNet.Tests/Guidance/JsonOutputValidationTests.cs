// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Newtonsoft.Json;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Guidance;

/// <summary>
/// Tests for the client-side schema backstop. Outputs from providers without on-wire schema
/// enforcement (GLM's json-object mode, guidance-disabled models) must be checked against the
/// generated schema: strict on required fields, types, and enums; lenient on extra properties
/// (harmless to deserialization) and on anything that can't be validated at all.
/// </summary>
public class JsonOutputValidationTests
{
    private sealed class SampleOutput
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("count")]
        public int Count { get; set; }
    }

    [Fact]
    public void ConformingOutput_Passes()
    {
        JsonOutputValidation.ConformsToType(
            """{"name":"acme","count":3}""", typeof(SampleOutput), "test")
            .Should().BeTrue();
    }

    [Fact]
    public void MissingRequiredField_Fails()
    {
        JsonOutputValidation.ConformsToType(
            """{"name":"acme"}""", typeof(SampleOutput), "test")
            .Should().BeFalse("json-auto schemas mark every property required; a missing field would deserialize to a silent default");
    }

    [Fact]
    public void WrongType_Fails()
    {
        JsonOutputValidation.ConformsToType(
            """{"name":"acme","count":"three"}""", typeof(SampleOutput), "test")
            .Should().BeFalse();
    }

    [Fact]
    public void ExtraProperties_AreTolerated()
    {
        JsonOutputValidation.ConformsToType(
            """{"name":"acme","count":3,"commentary":"models on unconstrained paths add these"}""",
            typeof(SampleOutput), "test")
            .Should().BeTrue("extra fields are harmless to deserialization and must not trigger retries");
    }

    [Fact]
    public void NullOrEmptyJson_IsSkipped()
    {
        JsonOutputValidation.ConformsToType(null, typeof(SampleOutput), "test").Should().BeTrue();
        JsonOutputValidation.ConformsToType("  ", typeof(SampleOutput), "test").Should().BeTrue();
    }

    [Fact]
    public void UnparseableJson_IsSkipped()
    {
        // The deserialization/remediation path owns malformed JSON; the schema backstop stays out.
        JsonOutputValidation.ConformsToType("{not json", typeof(SampleOutput), "test").Should().BeTrue();
    }
}
