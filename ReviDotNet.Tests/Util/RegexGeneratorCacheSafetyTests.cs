// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Util;

/// <summary>
/// Guards cache behavior that is easy to get wrong while making RegexAuto generation fast.
/// In particular, a process-wide cache must isolate wrapper options and must not root plugin
/// types from collectible assembly-load contexts.
/// </summary>
public sealed class RegexGeneratorCacheSafetyTests
{
    private sealed class CachePayload
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    [Fact]
    public async Task Concurrent_calls_do_not_cross_contaminate_wrapper_options()
    {
        const string body =
            "(?:\\{\\s*\"Name\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Count\"\\s*:\\s*(?:\\d+)\\s*\\})";
        var cases = new (bool ChainOfThought, string? StopToken, string Expected)[]
        {
            (false, null, body),
            (false, "<|eot_id|>", body + "<\\|eot_id\\|>"),
            (false, ".*stop", body + "\\.\\*stop"),
            (true, null, "Reasoning:\\s*(.*)\\nOutput:\\s*" + body),
            (true, "<|eot_id|>", "Reasoning:\\s*(.*)\\nOutput:\\s*" + body + "<\\|eot_id\\|>"),
            (true, ".*stop", "Reasoning:\\s*(.*)\\nOutput:\\s*" + body + "\\.\\*stop"),
        };

        Task<(int CaseIndex, string Regex)>[] calls = Enumerable.Range(0, 384)
            .Select(index => Task.Run(() =>
            {
                int caseIndex = index % cases.Length;
                var testCase = cases[caseIndex];
                string regex = RegexGenerator.FromObject(
                    typeof(CachePayload),
                    testCase.ChainOfThought,
                    testCase.StopToken);
                return (caseIndex, regex);
            }))
            .ToArray();

        foreach (var result in await Task.WhenAll(calls))
        {
            result.Regex.Should().Be(cases[result.CaseIndex].Expected);
        }
    }

    [Fact]
    public void Generating_a_regex_does_not_prevent_a_collectible_context_from_unloading()
    {
        byte[] modelAssembly = CompileCollectibleModel();
        WeakReference contextReference = LoadGenerateRegexAndUnload(modelAssembly);

        ForceCollection(contextReference);

        contextReference.IsAlive.Should().BeFalse(
            "a Type-keyed process-wide cache must not root unloadable plugin assemblies");
    }

    [Fact]
    public void Collectible_context_control_unloads_without_schema_generation()
    {
        byte[] modelAssembly = CompileCollectibleModel();
        WeakReference contextReference = LoadAndUnloadWithoutSchemaGeneration(modelAssembly);

        ForceCollection(contextReference);

        contextReference.IsAlive.Should().BeFalse(
            "the control proves the test fixture and runtime can unload this assembly");
    }

    [Fact]
    public void Collectible_DataMember_nullability_matrix_preserves_exact_regex()
    {
        byte[] modelAssembly = CompileCollectibleModel();
        WeakReference contextReference = LoadUseAndUnload(
            modelAssembly,
            static payloadType =>
            {
                Type matrixType = payloadType.Assembly.GetType(
                    "CollectibleModel.DataMemberNullabilityMatrixPayload",
                    throwOnError: true)!;
                RegexGenerator.FromObject(matrixType, chainOfThought: false).Should().Be(
                    "(?:\\{\\s*\"nullable_value_false\"\\s*:\\s*(?:\\d+|null),\\s*" +
                    "\"nullable_value_true\"\\s*:\\s*(?:\\d+|null),\\s*" +
                    "\"reference_false\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
                    "\"reference_true\"\\s*:\\s*(?:\"[^\"]*\"|null)\\s*\\})");
            });

        ForceCollection(contextReference);
        contextReference.IsAlive.Should().BeFalse(
            "the compatibility probe must not retain its collectible model assembly");
    }

    [Fact]
    public void Collectible_exception_and_KeyValuePair_contracts_preserve_exact_regexes()
    {
        byte[] modelAssembly = CompileCollectibleModel();
        WeakReference contextReference = LoadUseAndUnload(
            modelAssembly,
            static payloadType =>
            {
                const string derivedException =
                    "(?:\\{\\s*\"Code\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"Message\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"Data\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
                    "\"InnerException\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
                    "\"HelpLink\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"Source\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"HResult\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"StackTrace\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})";
                const string emptyObject = "(?:\\{\\s*\\s*\\})";
                const string emptyPair = "(?:)";
                const string pairList =
                    "(?:\\[\\s*(?:)(?:\\s*,\\s*(?:))*?\\s*\\])";

                Assembly assembly = payloadType.Assembly;
                Type derivedType = assembly.GetType(
                    "CollectibleModel.ProbeException",
                    throwOnError: true)!;
                Type baseHolderType = assembly.GetType(
                    "CollectibleModel.BaseExceptionHolder",
                    throwOnError: true)!;
                Type aggregateHolderType = assembly.GetType(
                    "CollectibleModel.AggregateExceptionHolder",
                    throwOnError: true)!;
                Type derivedHolderType = assembly.GetType(
                    "CollectibleModel.DerivedExceptionHolder",
                    throwOnError: true)!;
                Type pairHolderType = assembly.GetType(
                    "CollectibleModel.KeyValuePairHolder",
                    throwOnError: true)!;

                RegexGenerator.FromObject(derivedType, false).Should().Be(derivedException);
                RegexGenerator.FromObject(baseHolderType, false).Should().Be(
                    "(?:\\{\\s*\"Error\"\\s*:\\s*" + emptyObject + "\\s*\\})");
                RegexGenerator.FromObject(aggregateHolderType, false).Should().Be(
                    "(?:\\{\\s*\"Error\"\\s*:\\s*" + emptyObject + "\\s*\\})");
                RegexGenerator.FromObject(derivedHolderType, false).Should().Be(
                    "(?:\\{\\s*\"Error\"\\s*:\\s*" + derivedException + "\\s*\\})");
                RegexGenerator.FromObject(pairHolderType, false).Should().Be(
                    "(?:\\{\\s*\"Pair\"\\s*:\\s*" + emptyPair + ",\\s*" +
                    "\"Pairs\"\\s*:\\s*" + pairList + "\\s*\\})");
            });

        ForceCollection(contextReference);
        contextReference.IsAlive.Should().BeFalse(
            "the exception and KeyValuePair compatibility probe must unload with its model assembly");
    }

    [Fact]
    public void Collectible_contract_kinds_use_cached_exact_regex_and_still_unload()
    {
        byte[] modelAssembly = CompileCollectibleModel();
        WeakReference contextReference = LoadUseAndUnload(
            modelAssembly,
            static payloadType =>
            {
                Assembly assembly = payloadType.Assembly;
                Type objectEnumerable = assembly.GetType(
                    "CollectibleModel.ObjectEnumerable",
                    throwOnError: true)!;
                Type arrayEnumerable = assembly.GetType(
                    "CollectibleModel.ArrayEnumerable",
                    throwOnError: true)!;
                Type dictionaryOverride = assembly.GetType(
                    "CollectibleModel.DictionaryOverride",
                    throwOnError: true)!;
                Type invalidArrayOverride = assembly.GetType(
                    "CollectibleModel.InvalidArrayOverride",
                    throwOnError: true)!;
                Type invalidDictionaryOverride = assembly.GetType(
                    "CollectibleModel.InvalidDictionaryOverride",
                    throwOnError: true)!;
                Type newtonsoftConverted = assembly.GetType(
                    "CollectibleModel.NewtonsoftConvertedObject",
                    throwOnError: true)!;
                Type stjConverted = assembly.GetType(
                    "CollectibleModel.StjConvertedObject",
                    throwOnError: true)!;
                Type polymorphic = assembly.GetType(
                    "CollectibleModel.PolymorphicBase",
                    throwOnError: true)!;
                Type contractHolder = assembly.GetType(
                    "CollectibleModel.ContractHolder",
                    throwOnError: true)!;
                Type cachedPayload = assembly.GetType(
                    "CollectibleModel.CachedPayload",
                    throwOnError: true)!;
                Type countingStrategy = assembly.GetType(
                    "CollectibleModel.CountingNamingStrategy",
                    throwOnError: true)!;
                Type primitiveEdges = assembly.GetType(
                    "CollectibleModel.PrimitiveEdgeHolder",
                    throwOnError: true)!;
                Type converterContractHolder = assembly.GetType(
                    "CollectibleModel.ConverterContractHolder",
                    throwOnError: true)!;
                Type requiredDefault = assembly.GetType(
                    "CollectibleModel.RequiredDefaultPayload",
                    throwOnError: true)!;
                Type fieldsPayload = assembly.GetType(
                    "CollectibleModel.FieldsPayload",
                    throwOnError: true)!;
                Type nonContractDataMember = assembly.GetType(
                    "CollectibleModel.NonContractDataMemberPayload",
                    throwOnError: true)!;
                Type staticAnnotated = assembly.GetType(
                    "CollectibleModel.StaticAnnotatedPayload",
                    throwOnError: true)!;
                Type defaultOrder = assembly.GetType(
                    "CollectibleModel.DefaultOrderPayload",
                    throwOnError: true)!;
                Type derivedCollision = assembly.GetType(
                    "CollectibleModel.DerivedCollisionPayload",
                    throwOnError: true)!;
                Type sameClassCollision = assembly.GetType(
                    "CollectibleModel.SameClassCollisionPayload",
                    throwOnError: true)!;
                Type inheritedInterface = assembly.GetType(
                    "CollectibleModel.IDerivedInterfacePayload",
                    throwOnError: true)!;

                RegexGenerator.FromObject(objectEnumerable, false).Should().Be(
                    "(?:\\{\\s*\"Capacity\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"Count\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"label\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
                RegexGenerator.FromObject(arrayEnumerable, false).Should().Be(
                    "(?:\\[\\s*(?:\\d+)(?:\\s*,\\s*(?:\\d+))*?\\s*\\])");
                RegexGenerator.FromObject(dictionaryOverride, false).Should().Be(
                    "(?:\\{\\s*\\s*\\})");
                RegexGenerator.FromObject(invalidArrayOverride, false).Should().Be(
                    "(?:\\[\\s*\\s*\\])");
                Action invalidDictionary = () =>
                    RegexGenerator.FromObject(invalidDictionaryOverride, false);
                invalidDictionary.Should().Throw<Exception>()
                    .WithMessage("Type *InvalidDictionaryOverride is not a dictionary.");
                RegexGenerator.FromObject(newtonsoftConverted, false).Should().Be("(?:)");
                RegexGenerator.FromObject(stjConverted, false).Should().Be(
                    "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\})");
                RegexGenerator.FromObject(polymorphic, false).Should().Be(
                    "(?:\\{\\s*\"base_value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
                RegexGenerator.FromObject(contractHolder, false).Should().Be(
                    "(?:\\{\\s*\"object_enumerable\"\\s*:\\s*" +
                    "(?:\\{\\s*\"Capacity\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"Count\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"label\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\}),\\s*" +
                    "\"stj_converted\"\\s*:\\s*" +
                    "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\}),\\s*" +
                    "\"polymorphic\"\\s*:\\s*" +
                    "(?:\\{\\s*\"base_value\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})" +
                    "\\s*\\})");
                RegexGenerator.FromObject(primitiveEdges, false).Should().Be(
                    "(?:\\{\\s*\"big\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"db\"\\s*:\\s*(?:null),\\s*" +
                    "\"date\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"time\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"version\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"culture\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"intptr\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
                    "\"uintptr\"\\s*:\\s*(?:\\{\\s*\\s*\\})\\s*\\})");
                RegexGenerator.FromObject(converterContractHolder, false).Should().Be(
                    "(?:\\{\\s*\"read_only\"\\s*:\\s*" +
                    "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\}),\\s*" +
                    "\"type_converted\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"runtime_type\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"regex\"\\s*:\\s*(?:)\\s*\\})");
                RegexGenerator.FromObject(requiredDefault, false).Should().Be(
                    "(?:\\{\\s*\"unspecified_reference\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"explicit_reference\"\\s*:\\s*(?:\"[^\"]*\"|null),\\s*" +
                    "\"unspecified_nullable\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"explicit_nullable\"\\s*:\\s*(?:\\d+|null),\\s*" +
                    "\"explicit_value\"\\s*:\\s*(?:\\d+)\\s*\\})");
                RegexGenerator.FromObject(fieldsPayload, false).Should().Be(
                    "(?:\\{\\s*\"<AutoValue>k__BackingField\"\\s*:\\s*(?:\\d+)\\s*\\})");
                RegexGenerator.FromObject(nonContractDataMember, false).Should().Be(
                    "(?:\\{\\s*\"PublicData\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"Plain\"\\s*:\\s*(?:\\d+)\\s*\\})");
                RegexGenerator.FromObject(staticAnnotated, false).Should().Be(
                    "(?:\\{\\s*\"public_static_field\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"private_static_field\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"public_static_property\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"private_static_property\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"instance\"\\s*:\\s*(?:\\d+)\\s*\\})");
                RegexGenerator.FromObject(defaultOrder, false).Should().Be(
                    "(?:\\{\\s*\"FieldOne\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"FieldTwo\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"PropertyOne\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"PropertyTwo\"\\s*:\\s*(?:\\d+)\\s*\\})");
                RegexGenerator.FromObject(derivedCollision, false).Should().Be(
                    "(?:\\{\\s*\"same\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");
                Action duplicate = () => RegexGenerator.FromObject(sameClassCollision, false);
                duplicate.Should().Throw<JsonSerializationException>()
                    .WithMessage("A member with the name 'same' already exists on " +
                                 "'*SameClassCollisionPayload'. Use the JsonPropertyAttribute " +
                                 "to specify another name.");
                RegexGenerator.FromObject(inheritedInterface, false).Should().Be(
                    "(?:\\{\\s*\"DerivedValue\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"BaseValue\"\\s*:\\s*(?:\\d+)\\s*\\})");

                const string core = "(?:\\{\\s*\"value\"\\s*:\\s*(?:\\d+)\\s*\\})";
                RegexGenerator.FromObject(cachedPayload, false).Should().Be(core);
                Task<string>[] calls = Enumerable.Range(0, 128)
                    .Select(index => Task.Run(() => index % 2 == 0
                        ? RegexGenerator.FromObject(cachedPayload, false)
                        : RegexGenerator.FromObject(cachedPayload, true, "<stop>")))
                    .ToArray();
                Task.WaitAll(calls);
                for (int index = 0; index < calls.Length; index++)
                {
                    calls[index].Result.Should().Be(index % 2 == 0
                        ? core
                        : "Reasoning:\\s*(.*)\\nOutput:\\s*" + core + "<stop>");
                }

                countingStrategy.GetField("InstanceCount")!.GetValue(null).Should().Be(1);
            });

        ForceCollection(contextReference);
        contextReference.IsAlive.Should().BeFalse(
            "a cached collectible contract must not retain its assembly-load context");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadGenerateRegexAndUnload(byte[] modelAssembly)
    {
        return LoadUseAndUnload(
            modelAssembly,
            static payloadType =>
            {
                string regex = RegexGenerator.FromObject(payloadType, chainOfThought: false);
                regex.Should().Be(
                    "(?:\\{\\s*\"renamed\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
                    "\"count\"\\s*:\\s*(?:\\d+),\\s*" +
                    "\"optional\"\\s*:\\s*(?:\\d+|null),\\s*" +
                    "\"nested\"\\s*:\\s*(?:\\{\\s*\"value\"\\s*:\\s*" +
                    "(?:-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?)\\s*\\}),\\s*" +
                    "\"tags\"\\s*:\\s*(?:\\[\\s*(?:\"[^\"]*\")" +
                    "(?:\\s*,\\s*(?:\"[^\"]*\"))*?\\s*\\]),\\s*" +
                    "\"map\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
                    "\"field_name\"\\s*:\\s*(?:(true|false))\\s*\\})");

                Type optInType = payloadType.Assembly.GetType(
                    "CollectibleModel.JsonRequiredOptInPayload",
                    throwOnError: true)!;
                RegexGenerator.FromObject(optInType, chainOfThought: false).Should().Be(
                    "(?:\\{\\s*\"RequiredValue\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");

                Type typeNamingStrategyType = payloadType.Assembly.GetType(
                    "CollectibleModel.TypeNamingStrategyPayload",
                    throwOnError: true)!;
                RegexGenerator.FromObject(typeNamingStrategyType, chainOfThought: false).Should().Be(
                    "(?:\\{\\s*\"pascalName\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");

                Type memberNamingStrategyType = payloadType.Assembly.GetType(
                    "CollectibleModel.MemberNamingStrategyPayload",
                    throwOnError: true)!;
                RegexGenerator.FromObject(memberNamingStrategyType, chainOfThought: false).Should().Be(
                    "(?:\\{\\s*\"pascal_name\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})");

                Type nonNullableValueType = payloadType.Assembly.GetType(
                    "CollectibleModel.AllowNullNonNullableValuePayload",
                    throwOnError: true)!;
                RegexGenerator.FromObject(nonNullableValueType, chainOfThought: false).Should().Be(
                    "(?:\\{\\s*\"Value\"\\s*:\\s*(?:\\d+)\\s*\\})");
            });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndUnloadWithoutSchemaGeneration(byte[] modelAssembly)
    {
        return LoadUseAndUnload(modelAssembly, static _ => { });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadUseAndUnload(byte[] modelAssembly, Action<Type> useType)
    {
        var context = new AssemblyLoadContext(
            $"regex-generator-test-{Guid.NewGuid():N}",
            isCollectible: true);
        context.Resolving += static (_, assemblyName) =>
            assemblyName.Name == typeof(JsonPropertyAttribute).Assembly.GetName().Name
                ? typeof(JsonPropertyAttribute).Assembly
                : null;
        using var stream = new MemoryStream(modelAssembly, writable: false);
        Assembly assembly = context.LoadFromStream(stream);
        Type payloadType = assembly.GetType("CollectibleModel.Payload", throwOnError: true)!;

        useType(payloadType);

        var reference = new WeakReference(context, trackResurrection: false);
        context.Unload();
        return reference;
    }

    private static void ForceCollection(WeakReference reference)
    {
        for (int attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static byte[] CompileCollectibleModel()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Runtime.Serialization;
            using Newtonsoft.Json;
            using Newtonsoft.Json.Serialization;

            namespace CollectibleModel;

            public sealed class Payload
            {
                [JsonProperty("renamed", Order = 1)]
                public string Renamed { get; set; } = "";

                [JsonIgnore]
                public string Ignored { get; set; } = "";

                [JsonProperty("count", Order = 2)]
                public int? Count { get; set; }

                [JsonProperty("optional", Order = 3, Required = Required.AllowNull)]
                public int? Optional { get; set; }

                [JsonProperty("nested", Order = 4)]
                public Child Nested { get; set; } = new();

                [JsonProperty("tags", Order = 5)]
                public List<string> Tags { get; set; } = new();

                [JsonProperty("map", Order = 6)]
                public Dictionary<string, string> Map { get; set; } = new();

                [JsonProperty("field_name", Order = 7)]
                public bool Field;
            }

            public sealed class Child
            {
                [JsonProperty("value")]
                public double Value { get; set; }
            }

            [JsonObject(MemberSerialization.OptIn)]
            public sealed class JsonRequiredOptInPayload
            {
                [JsonRequired]
                public string RequiredValue { get; set; } = "";

                public string Excluded { get; set; } = "";
            }

            [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
            public sealed class TypeNamingStrategyPayload
            {
                public string PascalName { get; set; } = "";
            }

            public sealed class MemberNamingStrategyPayload
            {
                [JsonProperty(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
                public string PascalName { get; set; } = "";
            }

            public sealed class AllowNullNonNullableValuePayload
            {
                [JsonProperty(Required = Required.AllowNull)]
                public int Value { get; set; }
            }

            [DataContract]
            public sealed class DataMemberNullabilityMatrixPayload
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

            public sealed class ProbeException : System.Exception
            {
                public string Code { get; set; } = "";
            }

            public sealed class BaseExceptionHolder
            {
                public System.Exception Error { get; set; } = new();
            }

            public sealed class AggregateExceptionHolder
            {
                public System.AggregateException Error { get; set; } = new();
            }

            public sealed class DerivedExceptionHolder
            {
                public ProbeException Error { get; set; } = new();
            }

            public sealed class KeyValuePairHolder
            {
                public KeyValuePair<string, string> Pair { get; set; }
                public List<KeyValuePair<string, string>> Pairs { get; set; } = new();
            }

            [JsonObject]
            public sealed class ObjectEnumerable : List<int>
            {
                [JsonProperty("label", Order = 1)]
                public string Label { get; set; } = "";
            }

            [JsonArray]
            public sealed class ArrayEnumerable : List<int>
            {
                public string Label { get; set; } = "";
            }

            [JsonDictionary]
            public sealed class DictionaryOverride : Dictionary<string, int>
            {
                public string Label { get; set; } = "";
            }

            [JsonArray]
            public sealed class InvalidArrayOverride
            {
                public int Value { get; set; }
            }

            [JsonDictionary]
            public sealed class InvalidDictionaryOverride
            {
                public int Value { get; set; }
            }

            [JsonConverter(typeof(NewtonsoftAsStringConverter))]
            public sealed class NewtonsoftConvertedObject
            {
                [JsonProperty("value")]
                public int Value { get; set; }
            }

            public sealed class NewtonsoftAsStringConverter
                : JsonConverter<NewtonsoftConvertedObject>
            {
                public override NewtonsoftConvertedObject? ReadJson(
                    JsonReader reader,
                    System.Type objectType,
                    NewtonsoftConvertedObject? existingValue,
                    bool hasExistingValue,
                    JsonSerializer serializer) => throw new System.NotSupportedException();

                public override void WriteJson(
                    JsonWriter writer,
                    NewtonsoftConvertedObject? value,
                    JsonSerializer serializer) => writer.WriteValue(value?.Value.ToString());
            }

            [JsonConverter(typeof(ReadOnlyConverter))]
            public sealed class ReadOnlyConvertedObject
            {
                [JsonProperty("value")]
                public int Value { get; set; }
            }

            public sealed class ReadOnlyConverter : JsonConverter<ReadOnlyConvertedObject>
            {
                public override bool CanWrite => false;

                public override ReadOnlyConvertedObject? ReadJson(
                    JsonReader reader,
                    System.Type objectType,
                    ReadOnlyConvertedObject? existingValue,
                    bool hasExistingValue,
                    JsonSerializer serializer) => new();

                public override void WriteJson(
                    JsonWriter writer,
                    ReadOnlyConvertedObject? value,
                    JsonSerializer serializer) => throw new System.NotSupportedException();
            }

            [System.ComponentModel.TypeConverter(typeof(PocoTypeConverter))]
            public sealed class TypeConvertedObject
            {
                public int Value { get; set; }
            }

            public sealed class PocoTypeConverter : System.ComponentModel.TypeConverter
            {
                public override bool CanConvertTo(
                    System.ComponentModel.ITypeDescriptorContext? context,
                    System.Type? destinationType) =>
                    destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
            }

            public sealed class ConverterContractHolder
            {
                [JsonProperty("read_only", Order = 1)]
                public ReadOnlyConvertedObject ReadOnly { get; set; } = new();

                [JsonProperty("type_converted", Order = 2)]
                public TypeConvertedObject TypeConverted { get; set; } = new();

                [JsonProperty("runtime_type", Order = 3)]
                public System.Type RuntimeType { get; set; } = typeof(string);

                [JsonProperty("regex", Order = 4)]
                public System.Text.RegularExpressions.Regex Regex { get; set; } = new("x");
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

            [JsonObject(MemberSerialization.Fields)]
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

            [System.Text.Json.Serialization.JsonConverter(typeof(StjAsStringConverter))]
            public sealed class StjConvertedObject
            {
                [JsonProperty("value")]
                public int Value { get; set; }
            }

            public sealed class StjAsStringConverter
                : System.Text.Json.Serialization.JsonConverter<StjConvertedObject>
            {
                public override StjConvertedObject? Read(
                    ref System.Text.Json.Utf8JsonReader reader,
                    System.Type typeToConvert,
                    System.Text.Json.JsonSerializerOptions options) =>
                    throw new System.NotSupportedException();

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
                [JsonProperty("base_value")]
                public string BaseValue { get; set; } = "";
            }

            public sealed class PolymorphicDerived : PolymorphicBase
            {
                [JsonProperty("derived_value")]
                public int DerivedValue { get; set; }
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

            public sealed class PrimitiveEdgeHolder
            {
                [JsonProperty("big", Order = 1)]
                public System.Numerics.BigInteger Big { get; set; }

                [JsonProperty("db", Order = 2)]
                public System.DBNull Db { get; set; } = System.DBNull.Value;

                [JsonProperty("date", Order = 3)]
                public System.DateOnly Date { get; set; }

                [JsonProperty("time", Order = 4)]
                public System.TimeOnly Time { get; set; }

                [JsonProperty("version", Order = 5)]
                public System.Version Version { get; set; } = new();

                [JsonProperty("culture", Order = 6)]
                public System.Globalization.CultureInfo Culture { get; set; } =
                    System.Globalization.CultureInfo.InvariantCulture;

                [JsonProperty("intptr", Order = 7)]
                public System.IntPtr IntPtr { get; set; }

                [JsonProperty("uintptr", Order = 8)]
                public System.UIntPtr UIntPtr { get; set; }
            }

            public sealed class CountingNamingStrategy : NamingStrategy
            {
                public static int InstanceCount;

                public CountingNamingStrategy()
                {
                    System.Threading.Interlocked.Increment(ref InstanceCount);
                }

                protected override string ResolvePropertyName(string name) =>
                    name.ToLowerInvariant();
            }

            [JsonObject(NamingStrategyType = typeof(CountingNamingStrategy))]
            public sealed class CachedPayload
            {
                public int Value { get; set; }
            }
            """;

        string[] trustedPlatformAssemblies =
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? throw new InvalidOperationException("The runtime did not expose its reference assemblies.");

        IEnumerable<MetadataReference> references = trustedPlatformAssemblies
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(JsonPropertyAttribute).Assembly.Location));
        CSharpCompilation compilation = CSharpCompilation.Create(
            $"CollectibleModel.{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        result.Success.Should().BeTrue(
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        return output.ToArray();
    }
}
