// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Util;

/// <summary>
/// Characterizes the public <see cref="RegexGenerator"/> contract that predates the
/// System.Text.Json schema exporter. These tests intentionally preserve output details and
/// limitations so replacing the schema library cannot silently change callers' grammars. The
/// recursive-type test is the sole deliberate compatibility break: the legacy implementation
/// terminated the process with a stack overflow, so the replacement must fail safely instead.
/// </summary>
public class RegexGeneratorCompatibilityTests
{
    private const string StringPattern = "(?:\"[^\"]*\")";
    private const string IntegerPattern = "(?:\\d+)";
    private const string NumberPattern = "(?:-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?)";
    private const string BooleanPattern = "(?:(true|false))";

    private sealed class RenamedPayload
    {
        [JsonProperty("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonIgnore]
        public string Ignored { get; set; } = "";

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }

    private sealed class NestedPayload
    {
        [JsonProperty("address")]
        public AddressPayload Address { get; set; } = new();

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = [];
    }

    private sealed class AddressPayload
    {
        [JsonProperty("city")]
        public string City { get; set; } = "";

        [JsonProperty("zip_code")]
        public int ZipCode { get; set; }
    }

    private sealed class StringPayload
    {
        [JsonProperty("value")]
        public string Value { get; set; } = "";
    }

    private sealed class DecimalPayload
    {
        [JsonProperty("value")]
        public decimal Value { get; set; }
    }

    private sealed class NullablePayload
    {
        [JsonProperty("maybe")]
        public int? Maybe { get; set; }
    }

    private sealed class DictionaryPayload
    {
        [JsonProperty("values")]
        public Dictionary<string, string> Values { get; set; } = new();
    }

    private sealed class PublicFieldPayload
    {
        [JsonProperty("field_name")]
        public string FieldName = "";
    }

    private sealed class BarePublicFieldPayload
    {
        public string FieldName = "";
    }

    private sealed class EscapedNamePayload
    {
        [JsonProperty("a.b+")]
        public string Value { get; set; } = "";
    }

    private sealed class ExplicitOrderPayload
    {
        [JsonProperty("third", Order = 3)]
        public string Third { get; set; } = "";

        [JsonProperty("first", Order = 1)]
        public string First { get; set; } = "";

        [JsonProperty("second", Order = 2)]
        public string Second { get; set; } = "";
    }

    private sealed class EmptyPayload;

    private class BasePayload
    {
        [JsonProperty("base_value")]
        public string BaseValue { get; set; } = "";
    }

    private sealed class InheritedPayload : BasePayload
    {
        [JsonProperty("derived_value")]
        public int DerivedValue { get; set; }
    }

    private sealed class NullableEnumPayload
    {
        [JsonProperty("status", Required = Required.AllowNull)]
        public SampleStatus? Status { get; set; }
    }

    private sealed class NullableReferencePayload
    {
        [JsonProperty("name", Required = Required.AllowNull)]
        public string? Name { get; set; }
    }

    private sealed class SystemTextJsonNamedPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("modern_name")]
        public string ModernName { get; set; } = "";
    }

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class OptInPayload
    {
        [JsonProperty("included")]
        public string Included { get; set; } = "";

        public string Excluded { get; set; } = "";
    }

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class JsonRequiredOptInPayload
    {
        [JsonRequired]
        public string RequiredValue { get; set; } = "";

        public string Excluded { get; set; } = "";
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    private sealed class TypeNamingStrategyPayload
    {
        public string PascalName { get; set; } = "";
    }

    private sealed class MemberNamingStrategyPayload
    {
        [JsonProperty(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
        public string PascalName { get; set; } = "";
    }

    private sealed class AllowNullNonNullableValuePayload
    {
        [JsonProperty(Required = Required.AllowNull)]
        public int Value { get; set; }
    }

    private sealed class NonPublicPayload
    {
        [JsonProperty("private_value")]
        private int PrivateValue { get; set; }

        [JsonProperty("public_value")]
        public string PublicValue { get; set; } = "";
    }

    [DataContract]
    private sealed class DataContractPayload
    {
        [DataMember(Name = "data_member", Order = 1)]
        public string Included { get; set; } = "";

        public string Excluded { get; set; } = "";
    }

    [DataContract]
    private sealed class DataMemberNullabilityMatrixPayload
    {
        [DataMember(Name = "nullable_value_false", Order = 1, IsRequired = false)]
        public int? NullableValueFalse { get; set; }

        [DataMember(Name = "nullable_value_true", Order = 2, IsRequired = true)]
        public int? NullableValueTrue { get; set; }

        [DataMember(Name = "reference_false", Order = 3, IsRequired = false)]
        public string ReferenceFalse { get; set; } = "";

        [DataMember(Name = "reference_true", Order = 4, IsRequired = true)]
        public string ReferenceTrue { get; set; } = "";
    }

    private sealed class RecursivePayload
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("next")]
        public RecursivePayload? Next { get; set; }
    }

    private enum SampleStatus
    {
        Pending,
        Complete
    }

    [Fact]
    public void FromObject_TypeAndInstanceOverloads_AreIdentical()
    {
        string fromType = RegexGenerator.FromObject(typeof(RenamedPayload), false);
        string fromInstance = RegexGenerator.FromObject(new RenamedPayload(), false);

        fromInstance.Should().Be(fromType);
    }

    [Fact]
    public void FromObject_RenamedAndIgnoredProperties_PreservesExactRegex()
    {
        const string expected =
            "(?:\\{\\s*\"display_name\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"quantity\"\\s*:\\s*(?:\\d+)\\s*\\})";

        RegexGenerator.FromObject(typeof(RenamedPayload), false).Should().Be(expected);
    }

    [Fact]
    public void FromObject_NestedObjectAndArray_PreservesExactRegex()
    {
        const string expected =
            "(?:\\{\\s*\"address\"\\s*:\\s*" +
            "(?:\\{\\s*\"city\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"zip_code\"\\s*:\\s*(?:\\d+)\\s*\\}),\\s*" +
            "\"tags\"\\s*:\\s*" +
            "(?:\\[\\s*(?:\"[^\"]*\")(?:\\s*,\\s*(?:\"[^\"]*\"))*?\\s*\\])" +
            "\\s*\\})";

        RegexGenerator.FromObject(typeof(NestedPayload), false).Should().Be(expected);
    }

    [Theory]
    [InlineData(typeof(string), StringPattern)]
    [InlineData(typeof(char), StringPattern)]
    [InlineData(typeof(Guid), StringPattern)]
    [InlineData(typeof(DateTime), StringPattern)]
    [InlineData(typeof(DateTimeOffset), StringPattern)]
    [InlineData(typeof(TimeSpan), StringPattern)]
    [InlineData(typeof(Uri), StringPattern)]
    [InlineData(typeof(byte[]), StringPattern)]
    [InlineData(typeof(SampleStatus), StringPattern)]
    [InlineData(typeof(sbyte), IntegerPattern)]
    [InlineData(typeof(byte), IntegerPattern)]
    [InlineData(typeof(short), IntegerPattern)]
    [InlineData(typeof(ushort), IntegerPattern)]
    [InlineData(typeof(int), IntegerPattern)]
    [InlineData(typeof(int?), IntegerPattern)]
    [InlineData(typeof(uint), IntegerPattern)]
    [InlineData(typeof(long), IntegerPattern)]
    [InlineData(typeof(ulong), IntegerPattern)]
    [InlineData(typeof(float), NumberPattern)]
    [InlineData(typeof(double), NumberPattern)]
    [InlineData(typeof(decimal), NumberPattern)]
    [InlineData(typeof(bool), BooleanPattern)]
    [InlineData(typeof(bool?), BooleanPattern)]
    public void FromObject_PrimitiveTypes_PreserveExactRegex(Type type, string expected)
    {
        RegexGenerator.FromObject(type, false).Should().Be(expected);
    }

    [Fact]
    public void FromObject_Array_PreservesExactRegex()
    {
        const string expected =
            "(?:\\[\\s*(?:\\d+)(?:\\s*,\\s*(?:\\d+))*?\\s*\\])";

        RegexGenerator.FromObject(typeof(int[]), false).Should().Be(expected);
    }

    [Fact]
    public void FromObject_RootObject_PreservesExactMultiTypeRegex()
    {
        const string expected =
            "(?:\\{\\s*\\s*\\}|\\[\\s*\\s*\\]|\"[^\"]*\"|\\d+|" +
            "-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?|(true|false))";

        RegexGenerator.FromObject(typeof(object), false).Should().Be(expected);
        expected.Should().HaveLength(79);
    }

    [Fact]
    public void FromObject_EmptyObject_PreservesExactRegex()
    {
        RegexGenerator.FromObject(typeof(EmptyPayload), false)
            .Should().Be("(?:\\{\\s*\\s*\\})");
    }

    [Fact]
    public void FromObject_PublicField_PreservesNewtonsoftFieldContract()
    {
        RegexGenerator.FromObject(typeof(PublicFieldPayload), false)
            .Should().Be("(?:\\{\\s*\"field_name\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_UnannotatedPublicField_PreservesNewtonsoftFieldContract()
    {
        RegexGenerator.FromObject(typeof(BarePublicFieldPayload), false)
            .Should().Be("(?:\\{\\s*\"FieldName\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_RegexMetacharactersInPropertyName_AreEscaped()
    {
        RegexGenerator.FromObject(typeof(EscapedNamePayload), false)
            .Should().Be("(?:\\{\\s*\"a\\.b\\+\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_ExplicitJsonPropertyOrder_ControlsRegexOrder()
    {
        const string expected =
            "(?:\\{\\s*\"first\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"second\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"third\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})";

        RegexGenerator.FromObject(typeof(ExplicitOrderPayload), false).Should().Be(expected);
    }

    [Fact]
    public void FromObject_InheritedProperties_PreservesExactOrder()
    {
        const string expected =
            "(?:\\{\\s*\"derived_value\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"base_value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})";

        RegexGenerator.FromObject(typeof(InheritedPayload), false).Should().Be(expected);
    }

    [Fact]
    public void FromObject_NullableEnumAndReference_PreserveNullUnion()
    {
        RegexGenerator.FromObject(typeof(NullableEnumPayload), false)
            .Should().Be("(?:\\{\\s*\"status\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
        RegexGenerator.FromObject(typeof(NullableReferencePayload), false)
            .Should().Be("(?:\\{\\s*\"name\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
    }

    [Fact]
    public void FromObject_SystemTextJsonNameAttribute_IsIgnoredByLegacyContract()
    {
        RegexGenerator.FromObject(typeof(SystemTextJsonNamedPayload), false)
            .Should().Be("(?:\\{\\s*\"ModernName\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_JsonObjectOptIn_ExcludesUnannotatedProperties()
    {
        RegexGenerator.FromObject(typeof(OptInPayload), false)
            .Should().Be("(?:\\{\\s*\"included\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_JsonRequired_IsAnExplicitOptInMember()
    {
        RegexGenerator.FromObject(typeof(JsonRequiredOptInPayload), false)
            .Should().Be("(?:\\{\\s*\"RequiredValue\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_JsonObjectNamingStrategy_ControlsPropertyName()
    {
        RegexGenerator.FromObject(typeof(TypeNamingStrategyPayload), false)
            .Should().Be("(?:\\{\\s*\"pascalName\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_JsonPropertyNamingStrategy_ControlsPropertyName()
    {
        RegexGenerator.FromObject(typeof(MemberNamingStrategyPayload), false)
            .Should().Be("(?:\\{\\s*\"pascal_name\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void FromObject_AllowNullOnNonNullableValueType_RemainsNonNullable()
    {
        RegexGenerator.FromObject(typeof(AllowNullNonNullableValuePayload), false)
            .Should().Be("(?:\\{\\s*\"Value\"\\s*:\\s*(?:\\d+)\\s*\\})");
    }

    [Fact]
    public void FromObject_NonPublicJsonProperty_IsIncluded()
    {
        const string expected =
            "(?:\\{\\s*\"private_value\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"public_value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})";

        RegexGenerator.FromObject(typeof(NonPublicPayload), false).Should().Be(expected);
    }

    [Fact]
    public void FromObject_DataMemberContract_IsHonored()
    {
        RegexGenerator.FromObject(typeof(DataContractPayload), false)
            .Should().Be("(?:\\{\\s*\"data_member\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
    }

    [Fact]
    public void FromObject_DataMemberNullabilityMatrix_PreservesExactRegex()
    {
        const string expected =
            "(?:\\{\\s*\"nullable_value_false\"\\s*:\\s*(?:\\d+|null),\\s*" +
            "\"nullable_value_true\"\\s*:\\s*(?:\\d+|null),\\s*" +
            "\"reference_false\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
            "\"reference_true\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})";

        RegexGenerator.FromObject(typeof(DataMemberNullabilityMatrixPayload), false)
            .Should().Be(expected);
    }

    [Fact]
    public void FromObject_ChainOfThought_PreservesExactPrefix()
    {
        const string expected =
            "Reasoning:\\s*(.*)\\nOutput:\\s*" +
            "(?:\\{\\s*\"value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})";

        RegexGenerator.FromObject(typeof(StringPayload), true).Should().Be(expected);
    }

    [Fact]
    public void FromObject_StopToken_IsRegexEscapedAndAppendedWithoutAnchor()
    {
        const string expected =
            "(?:\\{\\s*\"value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})" +
            "stop\\.\\*\\(x\\)";

        RegexGenerator.FromObject(typeof(StringPayload), false, "stop.*(x)")
            .Should().Be(expected);
    }

    [Fact]
    public void FromObject_NullOrEmptyStopToken_AppendsNothing()
    {
        string baseline = RegexGenerator.FromObject(typeof(StringPayload), false);

        RegexGenerator.FromObject(typeof(StringPayload), false, null).Should().Be(baseline);
        RegexGenerator.FromObject(typeof(StringPayload), false, "").Should().Be(baseline);
    }

    [Fact]
    public void MatchesSchema_IsIntentionallyUnanchored()
    {
        var payload = new StringPayload { Value = "ok" };

        RegexGenerator.MatchesSchema("prefix {\"value\":\"ok\"} suffix", payload, false)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesSchema_ObjectPropertiesAreMandatoryAndOrdered()
    {
        var payload = new RenamedPayload();

        RegexGenerator.MatchesSchema("{\"display_name\":\"x\"}", payload, false)
            .Should().BeFalse();
        RegexGenerator.MatchesSchema("{\"quantity\":1,\"display_name\":\"x\"}", payload, false)
            .Should().BeFalse();
        RegexGenerator.MatchesSchema("{\"display_name\":\"x\",\"quantity\":1}", payload, false)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesSchema_ArrayRequiresOneOrMoreItems()
    {
        int[] payload = [];

        RegexGenerator.MatchesSchema("[]", payload, false).Should().BeFalse();
        RegexGenerator.MatchesSchema("[1, 2,3]", payload, false).Should().BeTrue();
    }

    [Fact]
    public void MatchesSchema_IntegerRetainsUnsignedOnlyBehavior()
    {
        var payload = new RenamedPayload();

        RegexGenerator.MatchesSchema("{\"display_name\":\"x\",\"quantity\":-1}", payload, false)
            .Should().BeFalse();
        RegexGenerator.MatchesSchema("{\"display_name\":\"x\",\"quantity\":1}", payload, false)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.25")]
    [InlineData("6.02e23")]
    [InlineData("6E-2")]
    public void MatchesSchema_NumberRetainsDecimalAndExponentForms(string number)
    {
        var payload = new DecimalPayload();

        RegexGenerator.MatchesSchema($"{{\"value\":{number}}}", payload, false)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesSchema_EnumRetainsGenericStringBehavior()
    {
        RegexGenerator.MatchesSchema("\"not-a-declared-enum-value\"", SampleStatus.Pending, false)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesSchema_ChainOfThoughtIsCaseSensitiveAndSingleLine()
    {
        var payload = new StringPayload { Value = "ok" };

        RegexGenerator.MatchesSchema(
                "Reasoning: one line\nOutput: {\"value\":\"ok\"}", payload, true)
            .Should().BeTrue();
        RegexGenerator.MatchesSchema(
                "reasoning: one line\nOutput: {\"value\":\"ok\"}", payload, true)
            .Should().BeFalse();
        RegexGenerator.MatchesSchema(
                "Reasoning: line one\nline two\nOutput: {\"value\":\"ok\"}", payload, true)
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesSchema_StringPatternRetainsLegacyEscapingLimitations()
    {
        var payload = new StringPayload();

        RegexGenerator.MatchesSchema("{\"value\":\"line one\nline two\"}", payload, false)
            .Should().BeTrue("the legacy negated-character class accepts raw newlines");
        RegexGenerator.MatchesSchema("{\"value\":\"quoted \\\"value\\\"\"}", payload, false)
            .Should().BeFalse("the legacy pattern does not understand JSON escape sequences");
    }

    [Fact]
    public void FromObject_NullableValue_DefaultDisallowNullExcludesNull()
    {
        string regex = RegexGenerator.FromObject(typeof(NullablePayload), false);

        regex.Should().Be("(?:\\{\\s*\"maybe\"\\s*:\\s*(?:\\d+)\\s*\\})");
    }

    [Fact]
    public void FromObject_Dictionary_PreservesCurrentAdditionalPropertiesLimitation()
    {
        string regex = RegexGenerator.FromObject(typeof(DictionaryPayload), false);

        regex.Should().Be("(?:\\{\\s*\"values\"\\s*:\\s*(?:\\{\\s*\\s*\\})\\s*\\})");
    }

    [Fact]
    public void FromObject_NullInstance_ThrowsNullReferenceException()
    {
        Action generate = () => RegexGenerator.FromObject((object)null!, false);

        generate.Should().Throw<NullReferenceException>();
    }

    [Fact]
    public void FromObject_RecursiveType_ThrowsCatchableNotSupportedExceptionInsteadOfStackOverflow()
    {
        Action generate = () => RegexGenerator.FromObject(typeof(RecursivePayload), false);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(generate);
        exception.Message.Should().MatchRegex(
            "(?i)(recursive|circular)",
            "callers need an actionable explanation instead of process termination");
    }

    [Fact]
    public async Task FromObject_ConcurrentRepeatedCalls_AreStableAndDoNotThrow()
    {
        const string expected =
            "(?:\\{\\s*\"display_name\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"quantity\"\\s*:\\s*(?:\\d+)\\s*\\})";

        Task<string>[] calls = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => RegexGenerator.FromObject(typeof(RenamedPayload), false)))
            .ToArray();

        string[] results = await Task.WhenAll(calls);
        results.Should().OnlyContain(regex => regex == expected);
    }
}
