// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Dynamic;
using System.Globalization;
using System.Numerics;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Util;

/// <summary>
/// Freezes contract cases captured from Newtonsoft.Json.Schema 4.0.1. Newtonsoft contracts
/// remain authoritative even when System.Text.Json attributes are also present.
/// </summary>
public sealed class RegexGeneratorContractKindCompatibilityTests
{
    [Newtonsoft.Json.JsonObject]
    public sealed class ObjectEnumerable : List<int>
    {
        [JsonProperty("label", Order = 1)] public string Label { get; set; } = "";
    }

    [Newtonsoft.Json.JsonArray]
    public sealed class ArrayEnumerable : List<int>
    {
        public string Label { get; set; } = "";
    }

    [Newtonsoft.Json.JsonDictionary]
    public sealed class DictionaryOverride : Dictionary<string, int>
    {
        public string Label { get; set; } = "";
    }

    [Newtonsoft.Json.JsonArray]
    public sealed class InvalidArrayOverride
    {
        public int Value { get; set; }
    }

    [Newtonsoft.Json.JsonDictionary]
    public sealed class InvalidDictionaryOverride
    {
        public int Value { get; set; }
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(StjAsStringConverter))]
    public sealed class StjConvertedObject
    {
        [JsonProperty("value")] public int Value { get; set; }
    }

    public sealed class StjAsStringConverter
        : System.Text.Json.Serialization.JsonConverter<StjConvertedObject>
    {
        public override StjConvertedObject? Read(
            ref System.Text.Json.Utf8JsonReader reader,
            Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(
            System.Text.Json.Utf8JsonWriter writer,
            StjConvertedObject value,
            System.Text.Json.JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value.ToString());
    }

    [System.Text.Json.Serialization.JsonPolymorphic]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(PolymorphicDerived), "derived")]
    public class PolymorphicBase
    {
        [JsonProperty("base_value")] public string BaseValue { get; set; } = "";
    }

    public sealed class PolymorphicDerived : PolymorphicBase
    {
        [JsonProperty("derived_value")] public int DerivedValue { get; set; }
    }

    public sealed class ContractHolder
    {
        [JsonProperty("object_enumerable", Order = 1)]
        public ObjectEnumerable ObjectEnumerable { get; set; } = new();

        [JsonProperty("stj_converted", Order = 2)]
        public StjConvertedObject StjConverted { get; set; } = new();

        [JsonProperty("polymorphic", Order = 3)]
        public PolymorphicBase Polymorphic { get; set; } = new();
    }

    [Newtonsoft.Json.JsonConverter(typeof(NewtonsoftAsStringConverter))]
    public sealed class NewtonsoftConvertedObject
    {
        [JsonProperty("value")] public int Value { get; set; }
    }

    public sealed class NewtonsoftAsStringConverter
        : Newtonsoft.Json.JsonConverter<NewtonsoftConvertedObject>
    {
        public override NewtonsoftConvertedObject? ReadJson(
            JsonReader reader, Type objectType, NewtonsoftConvertedObject? existingValue,
            bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer) =>
            throw new NotSupportedException();

        public override void WriteJson(
            JsonWriter writer, NewtonsoftConvertedObject? value,
            Newtonsoft.Json.JsonSerializer serializer) => writer.WriteValue(value?.Value.ToString());
    }

    public sealed class MemberConverterHolder
    {
        [Newtonsoft.Json.JsonConverter(typeof(IntAsStringConverter))]
        public int Value { get; set; }
    }

    public sealed class ItemConverterHolder
    {
        [JsonProperty(ItemConverterType = typeof(IntAsStringConverter))]
        public List<int> Values { get; set; } = [];
    }

    [Newtonsoft.Json.JsonArray(ItemConverterType = typeof(IntAsStringConverter))]
    public sealed class ArrayWithItemConverter : List<int>;

    [Newtonsoft.Json.JsonDictionary(ItemConverterType = typeof(IntAsStringConverter))]
    public sealed class DictionaryWithItemConverter : Dictionary<string, int>;

    public sealed class IntAsStringConverter : Newtonsoft.Json.JsonConverter<int>
    {
        public override int ReadJson(
            JsonReader reader, Type objectType, int existingValue, bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer) => throw new NotSupportedException();

        public override void WriteJson(
            JsonWriter writer, int value, Newtonsoft.Json.JsonSerializer serializer) =>
            writer.WriteValue(value.ToString());
    }

    [Newtonsoft.Json.JsonConverter(typeof(ReadOnlyConverter))]
    public sealed class ReadOnlyConvertedObject
    {
        [JsonProperty("value")] public int Value { get; set; }
    }

    public sealed class ReadOnlyConverter
        : Newtonsoft.Json.JsonConverter<ReadOnlyConvertedObject>
    {
        public override bool CanWrite => false;

        public override ReadOnlyConvertedObject? ReadJson(
            JsonReader reader, Type objectType, ReadOnlyConvertedObject? existingValue,
            bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer) => new();

        public override void WriteJson(
            JsonWriter writer, ReadOnlyConvertedObject? value,
            Newtonsoft.Json.JsonSerializer serializer) => throw new NotSupportedException();
    }

    [TypeConverter(typeof(PocoTypeConverter))]
    public sealed class TypeConvertedObject
    {
        public int Value { get; set; }
    }

    public sealed class PocoTypeConverter : TypeConverter
    {
        public override bool CanConvertTo(
            ITypeDescriptorContext? context,
            Type? destinationType) =>
            destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    }

    public sealed class ConverterContractHolder
    {
        [JsonProperty("read_only", Order = 1)]
        public ReadOnlyConvertedObject ReadOnly { get; set; } = new();

        [JsonProperty("type_converted", Order = 2)]
        public TypeConvertedObject TypeConverted { get; set; } = new();

        [JsonProperty("runtime_type", Order = 3)]
        public Type RuntimeType { get; set; } = typeof(string);

        [JsonProperty("regex", Order = 4)]
        public Regex Regex { get; set; } = new("x");
    }

    public sealed class RequiredDefaultPayload
    {
        [JsonProperty("unspecified_reference", Order = 1)]
        public string UnspecifiedReference { get; set; } = "";

        [JsonProperty("explicit_reference", Order = 2, Required = Required.Default)]
        public string ExplicitReference { get; set; } = "";

        [JsonProperty("unspecified_nullable", Order = 3)]
        public int? UnspecifiedNullable { get; set; }

        [JsonProperty("explicit_nullable", Order = 4, Required = Required.Default)]
        public int? ExplicitNullable { get; set; }

        [JsonProperty("explicit_value", Order = 5, Required = Required.Default)]
        public int ExplicitValue { get; set; }
    }

    [Newtonsoft.Json.JsonObject(MemberSerialization.Fields)]
    public sealed class FieldsPayload
    {
        [JsonProperty("annotated")]
        public int AutoValue { get; set; }
    }

    public sealed class NonContractDataMemberPayload
    {
        [DataMember(Name = "renamed", Order = 10)]
        public string PublicData { get; set; } = "";

        [DataMember(Name = "private", Order = 1)]
        private int PrivateData { get; set; }

        public int Plain { get; set; }
    }

    public sealed class StaticAnnotatedPayload
    {
        [JsonProperty("public_static_field", Order = 1)]
        public static int PublicStaticField;

        [JsonProperty("private_static_field", Order = 2)]
        private static int PrivateStaticField;

        [JsonProperty("public_static_property", Order = 3)]
        public static int PublicStaticProperty { get; set; }

        [JsonProperty("private_static_property", Order = 4)]
        private static int PrivateStaticProperty { get; set; }

        [JsonProperty("instance", Order = 5)]
        public int Instance { get; set; }
    }

    public sealed class DefaultOrderPayload
    {
        public int PropertyOne { get; set; }
        public int FieldOne;
        public int PropertyTwo { get; set; }
        public int FieldTwo;
    }

    public class BaseCollisionPayload
    {
        [JsonProperty("same")]
        public int BaseValue { get; set; }
    }

    public sealed class DerivedCollisionPayload : BaseCollisionPayload
    {
        [JsonProperty("same")]
        public string DerivedValue { get; set; } = "";
    }

    public sealed class SameClassCollisionPayload
    {
        [JsonProperty("same")]
        public int First { get; set; }

        [JsonProperty("same")]
        public string Second { get; set; } = "";
    }

    public interface IBaseInterfacePayload
    {
        int BaseValue { get; set; }
    }

    public interface IDerivedInterfacePayload : IBaseInterfacePayload
    {
        string DerivedValue { get; set; }
    }

    public sealed class DynamicPayload : DynamicObject
    {
        public int Value { get; set; }
    }

    [Newtonsoft.Json.JsonObject(ItemRequired = Required.AllowNull)]
    public sealed class ItemRequiredAllowNullPayload
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public int? Maybe { get; set; }
        [JsonProperty(Required = Required.DisallowNull)] public string Strict { get; set; } = "";
    }

    [Newtonsoft.Json.JsonObject(ItemRequired = Required.Always)]
    public sealed class ItemRequiredAlwaysPayload
    {
        public string Name { get; set; } = "";
        public int? Maybe { get; set; }
        [JsonProperty(Required = Required.AllowNull)] public string Loose { get; set; } = "";
    }

    [Fact]
    public void Newtonsoft_container_contract_kinds_override_clr_collection_shape()
    {
        RegexGenerator.FromObject(typeof(ObjectEnumerable), false).Should().Be(
            "(?:\\{\\s*\"Capacity\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"Count\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"label\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(ArrayEnumerable), false).Should().Be(
            "(?:\\[\\s*(?:\\d+)(?:\\s*,\\s*(?:\\d+))*?\\s*\\])");
        RegexGenerator.FromObject(typeof(DictionaryOverride), false).Should().Be(
            "(?:\\{\\s*\\s*\\})");
        RegexGenerator.FromObject(typeof(InvalidArrayOverride), false).Should().Be(
            "(?:\\[\\s*\\s*\\])");

        Action invalidDictionary = () =>
            RegexGenerator.FromObject(typeof(InvalidDictionaryOverride), false);
        invalidDictionary.Should().Throw<Exception>()
            .WithMessage("Type *InvalidDictionaryOverride is not a dictionary.");
    }

    [Fact]
    public void SystemTextJson_metadata_is_ignored_for_root_and_nested_contracts()
    {
        RegexGenerator.FromObject(typeof(StjConvertedObject), false).Should().Be(
            "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(PolymorphicBase), false).Should().Be(
            "(?:\\{\\s*\"base_value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(ContractHolder), false).Should().Be(
            "(?:\\{\\s*\"object_enumerable\"\\s*:\\s*" +
            "(?:\\{\\s*\"Capacity\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"Count\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"label\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\}),\\s*" +
            "\"stj_converted\"\\s*:\\s*" +
            "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\}),\\s*" +
            "\"polymorphic\"\\s*:\\s*" +
            "(?:\\{\\s*\"base_value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})\\s*\\})");
    }

    [Fact]
    public void Only_type_level_Newtonsoft_converter_suppresses_shape()
    {
        RegexGenerator.FromObject(typeof(NewtonsoftConvertedObject), false).Should().Be("(?:)");
        RegexGenerator.FromObject(typeof(MemberConverterHolder), false).Should().Be(
            "(?:\\{\\s*\"Value\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(ItemConverterHolder), false).Should().Be(
            "(?:\\{\\s*\"Values\"\\s*:\\s*" +
            "(?:\\[\\s*(?:\\d+)(?:\\s*,\\s*(?:\\d+))*?\\s*\\])\\s*\\})");
        RegexGenerator.FromObject(typeof(ArrayWithItemConverter), false).Should().Be(
            "(?:\\[\\s*(?:\\d+)(?:\\s*,\\s*(?:\\d+))*?\\s*\\])");
        RegexGenerator.FromObject(typeof(DictionaryWithItemConverter), false).Should().Be(
            "(?:\\{\\s*\\s*\\})");
    }

    [Fact]
    public void Read_only_and_internal_converters_match_legacy_contracts()
    {
        RegexGenerator.FromObject(typeof(ReadOnlyConvertedObject), false).Should().Be(
            "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\})");

        Type[] internalConverterTypes =
        [
            typeof(Regex),
            typeof(DataTable),
            typeof(DataSet),
            typeof(XmlDocument),
            typeof(XDocument),
        ];
        foreach (Type type in internalConverterTypes)
        {
            RegexGenerator.FromObject(type, false).Should().Be("(?:)");
        }
    }

    [Fact]
    public void String_convertible_contracts_match_legacy_at_root_and_when_nested()
    {
        RegexGenerator.FromObject(typeof(Type), false).Should().Be("(?:\"[^\"]*\")");
        RegexGenerator.FromObject(typeof(TypeConvertedObject), false).Should().Be("(?:\"[^\"]*\")");
        RegexGenerator.FromObject(typeof(ConverterContractHolder), false).Should().Be(
            "(?:\\{\\s*\"read_only\"\\s*:\\s*" +
            "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\}),\\s*" +
            "\"type_converted\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"runtime_type\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"regex\"\\s*:\\s*(?:)\\s*\\})");
    }

    [Fact]
    public void Explicit_Required_Default_preserves_the_legacy_null_union()
    {
        RegexGenerator.FromObject(typeof(RequiredDefaultPayload), false).Should().Be(
            "(?:\\{\\s*\"unspecified_reference\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"explicit_reference\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
            "\"unspecified_nullable\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"explicit_nullable\"\\s*:\\s*(?:\\d+|null),\\s*" +
            "\"explicit_value\"\\s*:\\s*(?:\\d+)\\s*\\})");
    }

    [Fact]
    public void Fields_and_non_DataContract_DataMember_modes_match_legacy()
    {
        RegexGenerator.FromObject(typeof(FieldsPayload), false).Should().Be(
            "(?:\\{\\s*\"<AutoValue>k__BackingField\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(NonContractDataMemberPayload), false).Should().Be(
            "(?:\\{\\s*\"PublicData\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Plain\"\\s*:\\s*(?:\\d+)\\s*\\})");
    }

    [Fact]
    public void Static_order_collision_and_inherited_interface_members_match_legacy()
    {
        RegexGenerator.FromObject(typeof(StaticAnnotatedPayload), false).Should().Be(
            "(?:\\{\\s*\"public_static_field\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"private_static_field\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"public_static_property\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"private_static_property\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"instance\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(DefaultOrderPayload), false).Should().Be(
            "(?:\\{\\s*\"FieldOne\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"FieldTwo\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"PropertyOne\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"PropertyTwo\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(DerivedCollisionPayload), false).Should().Be(
            "(?:\\{\\s*\"same\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        Action duplicate = () => RegexGenerator.FromObject(typeof(SameClassCollisionPayload), false);
        duplicate.Should().Throw<JsonSerializationException>()
            .WithMessage("A member with the name 'same' already exists on '*SameClassCollisionPayload'. " +
                         "Use the JsonPropertyAttribute to specify another name.");
        RegexGenerator.FromObject(typeof(IDerivedInterfacePayload), false).Should().Be(
            "(?:\\{\\s*\"DerivedValue\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"BaseValue\"\\s*:\\s*(?:\\d+)\\s*\\})");
    }

    [Theory]
    [InlineData(typeof(JObject), "(?:)")]
    [InlineData(typeof(JArray), "(?:)")]
    [InlineData(typeof(JValue), "(?:)")]
    [InlineData(typeof(JToken), "(?:)")]
    [InlineData(typeof(DynamicPayload), "(?:)")]
    [InlineData(typeof(ExpandoObject), "(?:\\{\\s*\\s*\\})")]
    public void Linq_dynamic_and_dictionary_contracts_match_legacy(Type type, string expected)
    {
        RegexGenerator.FromObject(type, false).Should().Be(expected);
    }

    [Fact]
    public void JsonObject_ItemRequired_does_not_change_null_shape()
    {
        RegexGenerator.FromObject(typeof(ItemRequiredAllowNullPayload), false).Should().Be(
            "(?:\\{\\s*\"Name\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Count\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"Maybe\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"Strict\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(ItemRequiredAlwaysPayload), false).Should().Be(
            "(?:\\{\\s*\"Name\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Maybe\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"Loose\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
    }

    [Theory]
    [InlineData(typeof(BigInteger), "(?:\\d+)")]
    [InlineData(typeof(DBNull), "(?:null)")]
    [InlineData(typeof(DateOnly), "(?:\"[^\"]*\")")]
    [InlineData(typeof(TimeOnly), "(?:\"[^\"]*\")")]
    [InlineData(typeof(IntPtr), "(?:\\{\\s*\\s*\\})")]
    [InlineData(typeof(UIntPtr), "(?:\\{\\s*\\s*\\})")]
    [InlineData(typeof(Version), "(?:\"[^\"]*\")")]
    [InlineData(typeof(CultureInfo), "(?:\"[^\"]*\")")]
    public void Less_common_primitive_contracts_match_legacy(Type type, string expected)
    {
        RegexGenerator.FromObject(type, false).Should().Be(expected);
    }
}
