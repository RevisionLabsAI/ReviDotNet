// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using FluentAssertions;
using Newtonsoft.Json;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Util;

/// <summary>
/// Exact outputs captured from Newtonsoft.Json.Schema 4.0.1 for edge contracts that differ
/// subtly from a direct reflection model.
/// </summary>
public sealed class RegexGeneratorFinalParityTests
{
    [JsonConverter(typeof(ConvertedEnumConverter))]
    public enum ConvertedEnum { First, Second }

    public sealed class ConvertedEnumConverter : JsonConverter<ConvertedEnum>
    {
        public override ConvertedEnum ReadJson(
            JsonReader reader, Type objectType, ConvertedEnum existingValue,
            bool hasExistingValue, JsonSerializer serializer) => ConvertedEnum.First;

        public override void WriteJson(
            JsonWriter writer, ConvertedEnum value, JsonSerializer serializer) =>
            writer.WriteValue("converted");
    }

    public sealed class UnspecifiedOrderPayload
    {
        public int FirstField;
        [JsonProperty("renamed")] public int Annotated { get; set; }
        public int Plain { get; set; }
    }

    public sealed class StaticRequiredDefaultPayload
    {
        [JsonProperty("static_reference", Order = 1, Required = Required.Default)]
        public static string StaticReference { get; set; } = "";

        [JsonProperty("static_nullable", Order = 2, Required = Required.Default)]
        public static int? StaticNullable { get; set; }

        [JsonProperty("static_value", Order = 3, Required = Required.Default)]
        public static int StaticValue { get; set; }
    }

    [DataContract]
    public sealed class StaticDataMemberPayload
    {
        [DataMember(Name = "static_reference", Order = 1)]
        public static string StaticReference { get; set; } = "";

        [DataMember(Name = "static_nullable", Order = 2)]
        public static int? StaticNullable { get; set; }

        [DataMember(Name = "static_value", Order = 3)]
        public static int StaticValue { get; set; }
    }

    [DataContract]
    [JsonObject(MemberSerialization.OptOut)]
    public sealed class DataContractOptOutPayload
    {
        [DataMember(Name = "data_name")]
        public string DataValue { get; set; } = "";
        public int Plain { get; set; }
    }

    [DataContract]
    [JsonObject(MemberSerialization.Fields)]
    public sealed class DataContractFieldsPayload
    {
        [DataMember(Name = "data_name")]
        public int DataProperty { get; set; }
        public int PlainField;
    }

    public class HierarchyBasePayload
    {
        public int BaseField;
        public int BaseProperty { get; set; }
    }

    public sealed class HierarchyDerivedPayload : HierarchyBasePayload
    {
        public int DerivedField;
        public int DerivedProperty { get; set; }
    }

    public class VirtualBasePayload
    {
        [JsonProperty("base_name", Order = 1, Required = Required.AllowNull)]
        public virtual string Value { get; set; } = "";
    }

    public sealed class VirtualDerivedPayload : VirtualBasePayload
    {
        public override string Value { get; set; } = "";
    }

    public class RenamedVirtualBasePayload
    {
        [JsonProperty("base_name", Required = Required.AllowNull)]
        public virtual string Value { get; set; } = "";
    }

    public sealed class RenamedVirtualDerivedPayload : RenamedVirtualBasePayload
    {
        [JsonProperty("derived_name", Required = Required.DisallowNull)]
        public override string Value { get; set; } = "";
    }

    public class ExplicitDefaultVirtualBasePayload
    {
        [JsonProperty("base_default", Required = Required.Default)]
        public virtual string Value { get; set; } = "";
    }

    public sealed class ExplicitDefaultVirtualDerivedPayload : ExplicitDefaultVirtualBasePayload
    {
        public override string Value { get; set; } = "";
    }

    [JsonObject(MemberSerialization.OptIn)]
    public interface IInterfaceMappedPayload
    {
        [JsonProperty("interface_name", Required = Required.AllowNull)]
        string Value { get; set; }
    }

    public sealed class InterfaceMappedPayload : IInterfaceMappedPayload
    {
        public string Value { get; set; } = "";
        public int Excluded { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.SnakeCaseNamingStrategy))]
    public class MixedNamingBasePayload
    {
        public string BaseValue { get; set; } = "";
    }

    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
    public sealed class MixedNamingDerivedPayload : MixedNamingBasePayload
    {
        public string DerivedValue { get; set; } = "";
    }

    [DataContract]
    public sealed class CombinedAttributePayload
    {
        [JsonProperty]
        [DataMember(Name = "data_a", Order = 5)]
        public string A { get; set; } = "";

        [DataMember(Name = "data_b", Order = 1)]
        public string B { get; set; } = "";

        [JsonProperty("json_c", Order = 3, Required = Required.DisallowNull)]
        [DataMember(Name = "data_c", Order = 0)]
        public string C { get; set; } = "";
    }

    [DataContract]
    public sealed class OptInContradictoryIgnorePayload
    {
        [DataMember(Name = "data", Order = 1), IgnoreDataMember]
        public string Data { get; set; } = "";

        [JsonProperty("json", Order = 2), IgnoreDataMember]
        public string Json { get; set; } = "";

        [IgnoreDataMember]
        public string OnlyIgnore { get; set; } = "";

        [DataMember(Name = "json_ignored", Order = 3), JsonIgnore]
        public string JsonIgnored { get; set; } = "";
    }

    public sealed class OptOutContradictoryIgnorePayload
    {
        [DataMember(Name = "data"), IgnoreDataMember]
        public string Data { get; set; } = "";

        [JsonProperty("json"), IgnoreDataMember]
        public string Json { get; set; } = "";

        public int Plain { get; set; }
    }

    public sealed class NonGenericEnumerablePayload : IEnumerable
    {
        public IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }

    [TypeConverter(typeof(ContainerStringConverter))]
    public sealed class TypeConvertedEnumerablePayload : List<int>;

    [TypeConverter(typeof(ContainerStringConverter))]
    public sealed class TypeConvertedDictionaryPayload : Dictionary<string, int>;

    public sealed class ContainerStringConverter : TypeConverter
    {
        public override bool CanConvertTo(
            ITypeDescriptorContext? context,
            Type? destinationType) =>
            destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    }

    public sealed class CustomConvertiblePayload : IConvertible
    {
        public TypeCode GetTypeCode() => TypeCode.Object;
        public bool ToBoolean(IFormatProvider? provider) => throw new NotSupportedException();
        public byte ToByte(IFormatProvider? provider) => throw new NotSupportedException();
        public char ToChar(IFormatProvider? provider) => throw new NotSupportedException();
        public DateTime ToDateTime(IFormatProvider? provider) => throw new NotSupportedException();
        public decimal ToDecimal(IFormatProvider? provider) => throw new NotSupportedException();
        public double ToDouble(IFormatProvider? provider) => throw new NotSupportedException();
        public short ToInt16(IFormatProvider? provider) => throw new NotSupportedException();
        public int ToInt32(IFormatProvider? provider) => throw new NotSupportedException();
        public long ToInt64(IFormatProvider? provider) => throw new NotSupportedException();
        public sbyte ToSByte(IFormatProvider? provider) => throw new NotSupportedException();
        public float ToSingle(IFormatProvider? provider) => throw new NotSupportedException();
        public string ToString(IFormatProvider? provider) => "";
        public object ToType(Type conversionType, IFormatProvider? provider) =>
            throw new NotSupportedException();
        public ushort ToUInt16(IFormatProvider? provider) => throw new NotSupportedException();
        public uint ToUInt32(IFormatProvider? provider) => throw new NotSupportedException();
        public ulong ToUInt64(IFormatProvider? provider) => throw new NotSupportedException();
    }

    public sealed class AnnotationRequiredPayload
    {
        [System.ComponentModel.DataAnnotations.Required]
        [JsonProperty("reference", Order = 1, Required = Required.AllowNull)]
        public string Reference { get; set; } = "";

        [System.ComponentModel.DataAnnotations.Required]
        [JsonProperty("nullable", Order = 2, Required = Required.AllowNull)]
        public int? Nullable { get; set; }
    }

    public interface IAnnotationRequiredPayload
    {
        [System.ComponentModel.DataAnnotations.Required]
        [JsonProperty("interface_required", Required = Required.AllowNull)]
        string Value { get; set; }
    }

    public sealed class InterfaceAnnotationRequiredPayload : IAnnotationRequiredPayload
    {
        public string Value { get; set; } = "";
    }

    public sealed class CustomTable : System.Data.DataTable;
    public sealed class CustomDataSet : System.Data.DataSet;

    public sealed class CustomRegex : System.Text.RegularExpressions.Regex
    {
        public CustomRegex() : base("x") { }
    }

    public sealed class CustomXmlDocument : System.Xml.XmlDocument;
    public sealed class CustomXDocument : System.Xml.Linq.XDocument;

    public sealed class HiddenTargetException : Exception
    {
        public string Code { get; set; } = "";
        public new string TargetSite { get; set; } = "";
    }

    public sealed class OrdinaryTargetSitePayload
    {
        public string TargetSite { get; set; } = "";
    }

    public interface IInterfaceDefaultPayload
    {
        [JsonProperty("interface_default", Required = Required.Default)]
        string Value { get; set; }
    }

    public sealed class InterfaceDefaultPayload : IInterfaceDefaultPayload
    {
        public string Value { get; set; } = "";
    }

    [JsonArray]
    public interface IInterfaceArrayPayload : IEnumerable<int>;

    public sealed class InterfaceArrayPayload : List<int>, IInterfaceArrayPayload;

    [JsonConverter(typeof(InterfacePayloadConverter))]
    public interface IInterfaceConvertedPayload;

    public sealed class InterfaceConvertedPayload : IInterfaceConvertedPayload
    {
        public int Value { get; set; }
    }

    [JsonArray, JsonConverter(typeof(InterfacePayloadConverter))]
    public interface IInterfaceConvertedArrayPayload : IEnumerable<int>;

    public sealed class InterfaceConvertedArrayPayload
        : List<int>, IInterfaceConvertedArrayPayload;

    public sealed class InterfacePayloadConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => true;
        public override object? ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer) => null;
        public override void WriteJson(
            JsonWriter writer,
            object? value,
            JsonSerializer serializer) => writer.WriteValue("converted");
    }

    [JsonDictionary]
    public interface IValidInterfaceDictionaryPayload : IDictionary<string, int>;

    public sealed class ValidInterfaceDictionaryPayload
        : Dictionary<string, int>, IValidInterfaceDictionaryPayload;

    [JsonDictionary]
    public interface IInvalidInterfaceDictionaryPayload;

    public sealed class InvalidInterfaceDictionaryPayload : IInvalidInterfaceDictionaryPayload;

    public interface IDerivedAddedInterfacePayload
    {
        [JsonProperty("interface_added", Required = Required.AllowNull)]
        string Value { get; set; }
    }

    public class DerivedAddedBasePayload
    {
        public string Value { get; set; } = "";
    }

    public sealed class DerivedAddedInterfacePayload
        : DerivedAddedBasePayload, IDerivedAddedInterfacePayload;

    public interface IExplicitInterfacePayload
    {
        [JsonProperty("interface_explicit", Required = Required.AllowNull)]
        string Value { get; set; }
    }

    public sealed class ExplicitInterfacePayload : IExplicitInterfacePayload
    {
        string IExplicitInterfacePayload.Value { get; set; } = "";
    }

    [System.ComponentModel.DataAnnotations.MetadataType(
        typeof(MetadataPrecedencePayloadMetadata))]
    [JsonObject(MemberSerialization.OptOut)]
    public sealed class MetadataPrecedencePayload : IInterfaceMappedPayload
    {
        [JsonProperty("direct_value", Required = Required.DisallowNull)]
        public string Value { get; set; } = "";
        public int Excluded { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class MetadataPrecedencePayloadMetadata
    {
        [JsonProperty("metadata_value", Required = Required.Default)]
        public string Value { get; set; } = "";
    }

    [System.ComponentModel.DataAnnotations.MetadataType(
        typeof(MetadataDataContractPayloadMetadata))]
    public sealed class MetadataDataContractPayload
    {
        public string Value { get; set; } = "";
        public int Excluded { get; set; }
    }

    [DataContract]
    public sealed class MetadataDataContractPayloadMetadata
    {
        [DataMember(Name = "metadata_data")]
        public string Value { get; set; } = "";
    }

    [DataContract]
    public class InheritedDataContractBasePayload
    {
        [DataMember(Name = "base_data")]
        public string Data { get; set; } = "";
        public int BaseExcluded { get; set; }
    }

    public sealed class InheritedDataContractDerivedPayload
        : InheritedDataContractBasePayload
    {
        public int DerivedExcluded { get; set; }
    }

    public sealed class ByRefMembersPayload
    {
        private int _value;
        public ref int RefValue => ref _value;
        public Span<int> SpanValue => Span<int>.Empty;
        public int Normal { get; set; }
    }

    public struct CustomRefValue
    {
        public int Value { get; set; }
    }

    public sealed class CustomByRefMembersPayload
    {
        private CustomRefValue _value;
        public ref CustomRefValue RefValue => ref _value;
        public int Normal { get; set; }
    }

    public sealed class InvalidExtensionDataPayload
    {
        [JsonExtensionData]
        public int Extra { get; set; }
    }

    public sealed class ValidExtensionDataPayload
    {
        [JsonExtensionData]
        public IDictionary<string, object> Extra { get; set; } =
            new Dictionary<string, object>();
        public int Normal { get; set; }
    }

    public sealed class WriteOnlyExtensionDataPayload
    {
        [JsonExtensionData]
        public IDictionary<string, object> Extra { set { } }
    }

    public sealed class StaticInvalidExtensionDataPayload
    {
        [JsonExtensionData]
        public static int Extra { get; set; }
        public int Normal { get; set; }
    }

    public sealed class BothInvalidExtensionDataPayload
    {
        [JsonExtensionData]
        public int Extra { set { } }
    }

    public class BaseInvalidExtensionDataPayload
    {
        [JsonExtensionData]
        public int BaseExtra { get; set; }
    }

    public sealed class DerivedInvalidExtensionDataPayload : BaseInvalidExtensionDataPayload
    {
        [JsonExtensionData]
        public int DerivedExtra;
    }

    public sealed class DerivedFromBaseInvalidExtensionDataPayload
        : BaseInvalidExtensionDataPayload;

    public class VirtualBaseInvalidExtensionDataPayload
    {
        [JsonExtensionData]
        public virtual int Extra { get; set; }
    }

    public sealed class VirtualDerivedInvalidExtensionDataPayload
        : VirtualBaseInvalidExtensionDataPayload
    {
        public override int Extra { get; set; }
    }

    public sealed class ExtensionDataCollisionPayload
    {
        [JsonProperty("same")]
        public int First { get; set; }

        [JsonProperty("same")]
        public int Second { get; set; }

        [JsonExtensionData]
        public int Extra { get; set; }
    }

    public sealed class BackingFieldExtensionDataPayload
    {
        [field: JsonExtensionData]
        public int Extra { get; set; }
    }

    public sealed class ValidBackingFieldExtensionDataPayload
    {
        public int Normal { get; set; }

        [field: JsonExtensionData]
        public IDictionary<string, object> Extra { get; set; } =
            new Dictionary<string, object>();
    }

    public sealed class IndexerExtensionDataPayload
    {
        [JsonExtensionData]
        public int this[int index]
        {
            get => index;
            set { }
        }
    }

    public sealed class ValidIndexerExtensionDataPayload
    {
        public int Normal { get; set; }

        [JsonExtensionData]
        public IDictionary<string, object> this[int index]
        {
            get => new Dictionary<string, object>();
            set { }
        }
    }

    public sealed class SpanExtensionDataPayload
    {
        [JsonExtensionData]
        public Span<int> Extra => Span<int>.Empty;
    }

    public sealed class RefReturnExtensionDataPayload
    {
        private int _extra;

        [JsonExtensionData]
        public ref int Extra => ref _extra;
    }

    public interface IInterfaceExtensionDataPayload
    {
        [JsonExtensionData]
        int Extra { get; set; }
    }

    public sealed class InterfaceExtensionDataPayload : IInterfaceExtensionDataPayload
    {
        public int Extra { get; set; }
        public int Normal { get; set; }
    }

    [System.ComponentModel.DataAnnotations.MetadataType(
        typeof(MetadataExtensionDataPayloadMetadata))]
    public sealed class MetadataExtensionDataPayload
    {
        public int Extra { get; set; }
        public int Normal { get; set; }
    }

    public sealed class MetadataExtensionDataPayloadMetadata
    {
        [JsonExtensionData]
        public int Extra { get; set; }
    }

    public sealed class DisabledExtensionDataPayload
    {
        [JsonExtensionData(ReadData = false, WriteData = false)]
        public int Extra { get; set; }
    }

    public sealed class ValidDisabledExtensionDataPayload
    {
        [JsonExtensionData(ReadData = false, WriteData = false)]
        public IDictionary<string, object> Extra { get; set; } =
            new Dictionary<string, object>();
        public int Normal { get; set; }
    }

    public sealed class ReadOnlyExtensionDataPayload
    {
        [JsonExtensionData]
        public System.Collections.ObjectModel.ReadOnlyDictionary<
            string, Newtonsoft.Json.Linq.JToken> Extra { get; set; } = null!;
    }

    public sealed class ReadOnlyNoReadExtensionDataPayload
    {
        [JsonExtensionData(ReadData = false)]
        public System.Collections.ObjectModel.ReadOnlyDictionary<
            string, Newtonsoft.Json.Linq.JToken> Extra { get; set; } = null!;
        public int Normal { get; set; }
    }

    public sealed class LastNoReadExtensionDataPayload
    {
        [JsonExtensionData]
        public System.Collections.ObjectModel.ReadOnlyDictionary<
            string, Newtonsoft.Json.Linq.JToken> First { get; set; } = null!;

        [JsonExtensionData(ReadData = false)]
        public IDictionary<string, object> Last { get; set; } =
            new Dictionary<string, object>();

        public int Normal { get; set; }
    }

    public sealed class LastReadOnlyExtensionDataPayload
    {
        [JsonExtensionData(ReadData = false)]
        public IDictionary<string, object> First { get; set; } =
            new Dictionary<string, object>();

        [JsonExtensionData]
        public System.Collections.ObjectModel.ReadOnlyDictionary<
            string, Newtonsoft.Json.Linq.JToken> Last { get; set; } = null!;
    }

    public sealed class ValidThenInvalidExtensionDataPayload
    {
        [JsonExtensionData]
        public IDictionary<string, object> First { get; set; } =
            new Dictionary<string, object>();

        [JsonExtensionData]
        public int SecondInvalid { get; set; }
    }

    public class ValidExtensionDataBasePayload
    {
        [JsonExtensionData]
        public IDictionary<string, object> BaseExtra { get; set; } =
            new Dictionary<string, object>();
    }

    public sealed class ValidBaseInvalidDerivedExtensionDataPayload
        : ValidExtensionDataBasePayload
    {
        [JsonExtensionData]
        public int DerivedExtra;
    }

    public interface IInvalidFirstMultiDictionary
        : IDictionary<int, Newtonsoft.Json.Linq.JToken>, IDictionary<string, object>;

    public interface IGoodThenBadDictionary
        : IDictionary<string, Newtonsoft.Json.Linq.JToken>,
          IDictionary<int, Newtonsoft.Json.Linq.JToken>;

    public sealed class MultiDictionaryExtensionDataPayload
    {
        [JsonExtensionData]
        public IInvalidFirstMultiDictionary Extra { get; set; } = null!;
    }

    public sealed class GoodThenBadDictionaryExtensionDataPayload
    {
        [JsonExtensionData]
        public IGoodThenBadDictionary Extra { get; set; } = null!;
    }

    public interface IDirectInvalidExtensionDataInterface
    {
        [JsonExtensionData]
        int Extra { get; set; }
    }

    public interface IInheritedExtensionDataInterface
        : IDirectInvalidExtensionDataInterface;

    public sealed class GenericInvalidExtensionDataPayload<TFirst, TSecond>
    {
        [JsonExtensionData]
        public int Extra { get; set; }
    }

    [Fact]
    public void By_ref_like_and_extension_data_edges_match_legacy()
    {
        RegexGenerator.FromObject(typeof(ByRefMembersPayload), false).Should().Be(
            "(?:\\{\\s*\"RefValue\"\\s*:\\s*" +
            "(?:\\{\\s*\\s*\\}|\\[\\s*\\s*\\]|\"[^\"]*\"|\\d+|" +
            "-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?|(true|false)),\\s*" +
            "\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(CustomByRefMembersPayload), false).Should().Be(
            "(?:\\{\\s*\"RefValue\"\\s*:\\s*" +
            "(?:\\{\\s*\"Value\"\\s*:\\s*(?:\\d+)\\s*\\}),\\s*" +
            "\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(ValidExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(StaticInvalidExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");

        Action invalid = () =>
            RegexGenerator.FromObject(typeof(InvalidExtensionDataPayload), false);
        invalid.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on '{typeof(InvalidExtensionDataPayload)}'. " +
            "Member 'Extra' type must implement IDictionary<string, JToken>.");

        Action writeOnly = () =>
            RegexGenerator.FromObject(typeof(WriteOnlyExtensionDataPayload), false);
        writeOnly.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on '{typeof(WriteOnlyExtensionDataPayload)}'. " +
            "Member 'Extra' must have a getter.");
    }

    [Fact]
    public void Extension_data_discovery_and_validation_edges_match_legacy()
    {
        RegexGenerator.FromObject(typeof(InterfaceExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(MetadataExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(ValidIndexerExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(ValidDisabledExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(ReadOnlyNoReadExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(LastNoReadExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(ValidBackingFieldExtensionDataPayload), false).Should().Be(
            "(?:\\{\\s*\"Normal\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"Extra\"\\s*:\\s*(?:\\{\\s*\\s*\\})\\s*\\})");
        RegexGenerator.FromObject(typeof(IInheritedExtensionDataInterface), false).Should().Be(
            "(?:\\{\\s*\\s*\\})");

        string readOnlyDictionaryType = typeof(
            System.Collections.ObjectModel.ReadOnlyDictionary<
                string, Newtonsoft.Json.Linq.JToken>).ToString();
        Action readOnly = () =>
            RegexGenerator.FromObject(typeof(ReadOnlyExtensionDataPayload), false);
        readOnly.Should().ThrowExactly<ArgumentException>().WithMessage(
            $"Could not get constructor for {readOnlyDictionaryType}.");
        Action lastReadOnly = () =>
            RegexGenerator.FromObject(typeof(LastReadOnlyExtensionDataPayload), false);
        lastReadOnly.Should().ThrowExactly<ArgumentException>().WithMessage(
            $"Could not get constructor for {readOnlyDictionaryType}.");
        Action goodThenBadDictionary = () => RegexGenerator.FromObject(
            typeof(GoodThenBadDictionaryExtensionDataPayload), false);
        goodThenBadDictionary.Should().ThrowExactly<ArgumentException>().WithMessage(
            $"Could not get constructor for {typeof(IGoodThenBadDictionary)}.");

        Action bothBad = () =>
            RegexGenerator.FromObject(typeof(BothInvalidExtensionDataPayload), false);
        bothBad.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on '{typeof(BothInvalidExtensionDataPayload)}'. " +
            "Member 'Extra' must have a getter.");

        Action derivedOrder = () =>
            RegexGenerator.FromObject(typeof(DerivedInvalidExtensionDataPayload), false);
        derivedOrder.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on '{typeof(BaseInvalidExtensionDataPayload)}'. " +
            "Member 'BaseExtra' type must implement IDictionary<string, JToken>.");

        Action inheritedMessage = () =>
            RegexGenerator.FromObject(typeof(DerivedFromBaseInvalidExtensionDataPayload), false);
        inheritedMessage.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on '{typeof(BaseInvalidExtensionDataPayload)}'. " +
            "Member 'BaseExtra' type must implement IDictionary<string, JToken>.");

        Action virtualBase = () =>
            RegexGenerator.FromObject(typeof(VirtualDerivedInvalidExtensionDataPayload), false);
        virtualBase.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on " +
            $"'{typeof(VirtualBaseInvalidExtensionDataPayload)}'. Member 'Extra' type must " +
            "implement IDictionary<string, JToken>.");

        Action validThenInvalid = () =>
            RegexGenerator.FromObject(typeof(ValidThenInvalidExtensionDataPayload), false);
        validThenInvalid.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on " +
            $"'{typeof(ValidThenInvalidExtensionDataPayload)}'. Member 'SecondInvalid' type " +
            "must implement IDictionary<string, JToken>.");

        Action inheritedValidThenInvalid = () => RegexGenerator.FromObject(
            typeof(ValidBaseInvalidDerivedExtensionDataPayload),
            false);
        inheritedValidThenInvalid.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on " +
            $"'{typeof(ValidBaseInvalidDerivedExtensionDataPayload)}'. Member 'DerivedExtra' " +
            "type must implement IDictionary<string, JToken>.");

        Action collision = () =>
            RegexGenerator.FromObject(typeof(ExtensionDataCollisionPayload), false);
        collision.Should().ThrowExactly<JsonSerializationException>().WithMessage(
            $"A member with the name 'same' already exists on " +
            $"'{typeof(ExtensionDataCollisionPayload)}'. Use the JsonPropertyAttribute to " +
            "specify another name.");

        foreach ((Type Type, string Member) edge in new[]
                 {
                     (typeof(BackingFieldExtensionDataPayload), "<Extra>k__BackingField"),
                     (typeof(IndexerExtensionDataPayload), "Item"),
                     (typeof(SpanExtensionDataPayload), "Extra"),
                     (typeof(RefReturnExtensionDataPayload), "Extra"),
                     (typeof(DisabledExtensionDataPayload), "Extra"),
                     (typeof(MultiDictionaryExtensionDataPayload), "Extra"),
                 })
        {
            Action invalid = () => RegexGenerator.FromObject(edge.Type, false);
            invalid.Should().ThrowExactly<JsonException>().WithMessage(
                $"Invalid extension data attribute on '{edge.Type}'. Member '{edge.Member}' " +
                "type must implement IDictionary<string, JToken>.");
        }


        Action directInterface = () =>
            RegexGenerator.FromObject(typeof(IDirectInvalidExtensionDataInterface), false);
        directInterface.Should().ThrowExactly<JsonException>().WithMessage(
            $"Invalid extension data attribute on " +
            $"'{typeof(IDirectInvalidExtensionDataInterface)}'. Member 'Extra' type must " +
            "implement IDictionary<string, JToken>.");

        Type genericDefinition = typeof(GenericInvalidExtensionDataPayload<,>);
        Action genericDefinitionAction = () =>
            RegexGenerator.FromObject(genericDefinition, false);
        genericDefinitionAction.Should().ThrowExactly<ArgumentException>().WithMessage(
            "Invalid type owner for DynamicMethod.");

        Type partialOpenType = genericDefinition.MakeGenericType(
            typeof(int),
            genericDefinition.GetGenericArguments()[0]);
        Action partialOpenAction = () => RegexGenerator.FromObject(partialOpenType, false);
        partialOpenAction.Should().ThrowExactly<ArgumentException>().WithMessage(
            "Invalid type owner for DynamicMethod.");
    }

    [Fact]
    public void Contract_kind_precedence_edges_match_legacy()
    {
        RegexGenerator.FromObject(typeof(ConvertedEnum), false).Should().Be("(?:\"[^\"]*\")");
        RegexGenerator.FromObject(typeof(NonGenericEnumerablePayload), false).Should().Be(
            "(?:\\[\\s*\\s*\\])");
        RegexGenerator.FromObject(typeof(TypeConvertedEnumerablePayload), false).Should().Be(
            "(?:\\[\\s*(?:\\d+)(?:\\s*,\\s*(?:\\d+))*?\\s*\\])");
        RegexGenerator.FromObject(typeof(TypeConvertedDictionaryPayload), false).Should().Be(
            "(?:\\{\\s*\\s*\\})");
        RegexGenerator.FromObject(typeof(CustomConvertiblePayload), false).Should().Be(
            "(?:\\{\\s*\\s*\\}|\\[\\s*\\s*\\]|\"[^\"]*\"|\\d+|" +
            "-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?|(true|false))");
    }

    [Fact]
    public void Member_metadata_and_ordering_edges_match_legacy()
    {
        RegexGenerator.FromObject(typeof(UnspecifiedOrderPayload), false).Should().Be(
            "(?:\\{\\s*\"FirstField\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"renamed\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"Plain\"\\s*:\\s*(?:\\d+)\\s*\\})");

        const string staticNullability =
            "(?:\\{\\s*\"static_reference\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
            "\"static_nullable\"\\s*:\\s*(?:\\d+|null),\\s*" +
            "\"static_value\"\\s*:\\s*(?:\\d+)\\s*\\})";
        RegexGenerator.FromObject(typeof(StaticRequiredDefaultPayload), false)
            .Should().Be(staticNullability);
        RegexGenerator.FromObject(typeof(StaticDataMemberPayload), false)
            .Should().Be(staticNullability);

        RegexGenerator.FromObject(typeof(DataContractOptOutPayload), false).Should().Be(
            "(?:\\{\\s*\"data_name\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
            "\"Plain\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(DataContractFieldsPayload), false).Should().Be(
            "(?:\\{\\s*\"<DataProperty>k__BackingField\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"PlainField\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(HierarchyDerivedPayload), false).Should().Be(
            "(?:\\{\\s*\"DerivedField\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"BaseField\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"DerivedProperty\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"BaseProperty\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(VirtualDerivedPayload), false).Should().Be(
            "(?:\\{\\s*\"base_name\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
        RegexGenerator.FromObject(typeof(RenamedVirtualDerivedPayload), false).Should().Be(
            "(?:\\{\\s*\"derived_name\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(ExplicitDefaultVirtualDerivedPayload), false).Should().Be(
            "(?:\\{\\s*\"base_default\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
        RegexGenerator.FromObject(typeof(InterfaceMappedPayload), false).Should().Be(
            "(?:\\{\\s*\"interface_name\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
        RegexGenerator.FromObject(typeof(MixedNamingDerivedPayload), false).Should().Be(
            "(?:\\{\\s*\"derivedValue\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"base_value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(CombinedAttributePayload), false).Should().Be(
            "(?:\\{\\s*\"data_a\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"data_b\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
            "\"json_c\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(OptInContradictoryIgnorePayload), false).Should().Be(
            "(?:\\{\\s*\"data\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
            "\"json\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(OptOutContradictoryIgnorePayload), false).Should().Be(
            "(?:\\{\\s*\"Plain\"\\s*:\\s*(?:\\d+)\\s*\\})");
    }

    [Fact]
    public void Required_internal_converter_and_Exception_edges_match_legacy()
    {
        RegexGenerator.FromObject(typeof(AnnotationRequiredPayload), false).Should().Be(
            "(?:\\{\\s*\"reference\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"nullable\"\\s*:\\s*(?:\\d+)\\s*\\})");
        RegexGenerator.FromObject(typeof(InterfaceAnnotationRequiredPayload), false).Should().Be(
            "(?:\\{\\s*\"interface_required\"\\s*:\\s*" +
            "(?:\"[^\"]*\"|null)\\s*\\})");

        foreach (Type type in new[]
                 {
                     typeof(CustomTable),
                     typeof(CustomDataSet),
                     typeof(CustomXmlDocument),
                     typeof(CustomXDocument),
                 })
        {
            RegexGenerator.FromObject(type, false).Should().Be("(?:)");
        }

        RegexGenerator.FromObject(typeof(CustomRegex), false).Should().Be(
            "(?:\\{\\s*\"Options\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"RightToLeft\"\\s*:\\s*(?:(true|false)),\\s*" +
            "\"MatchTimeout\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(HiddenTargetException), false).Should().Be(
            "(?:\\{\\s*\"Code\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Message\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Data\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
            "\"InnerException\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
            "\"HelpLink\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Source\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"HResult\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"StackTrace\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(OrdinaryTargetSitePayload), false).Should().Be(
            "(?:\\{\\s*\"TargetSite\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
    }

    [Fact]
    public void Effective_interface_and_MetadataType_attributes_match_legacy()
    {
        RegexGenerator.FromObject(typeof(InterfaceDefaultPayload), false).Should().Be(
            "(?:\\{\\s*\"interface_default\"\\s*:\\s*" +
            "(?:\"[^\"]*\"|null)\\s*\\})");
        RegexGenerator.FromObject(typeof(InterfaceArrayPayload), false).Should().Be(
            "(?:\\[\\s*(?:\\d+)(?:\\s*,\\s*(?:\\d+))*?\\s*\\])");
        RegexGenerator.FromObject(typeof(InterfaceConvertedPayload), false).Should().Be("(?:)");
        RegexGenerator.FromObject(typeof(InterfaceConvertedArrayPayload), false)
            .Should().Be("(?:)");
        RegexGenerator.FromObject(typeof(ValidInterfaceDictionaryPayload), false).Should().Be(
            "(?:\\{\\s*\\s*\\})");
        Action invalidDictionary = () =>
            RegexGenerator.FromObject(typeof(InvalidInterfaceDictionaryPayload), false);
        invalidDictionary.Should().Throw<Exception>()
            .WithMessage("Type *InvalidInterfaceDictionaryPayload is not a dictionary.");

        RegexGenerator.FromObject(typeof(DerivedAddedInterfacePayload), false).Should().Be(
            "(?:\\{\\s*\"Value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
        RegexGenerator.FromObject(typeof(ExplicitInterfacePayload), false).Should().Be(
            "(?:\\{\\s*\\s*\\})");
        RegexGenerator.FromObject(typeof(MetadataPrecedencePayload), false).Should().Be(
            "(?:\\{\\s*\"metadata_value\"\\s*:\\s*" +
            "(?:\"[^\"]*\"|null)\\s*\\})");
        RegexGenerator.FromObject(typeof(MetadataDataContractPayload), false).Should().Be(
            "(?:\\{\\s*\"metadata_data\"\\s*:\\s*" +
            "(?:\"[^\"]*\"|null)\\s*\\})");
        RegexGenerator.FromObject(typeof(InheritedDataContractDerivedPayload), false).Should().Be(
            "(?:\\{\\s*\"base_data\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
    }
}
