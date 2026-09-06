// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Revi;

public class Person
{
    public string Name { get; set; }
    public int Age { get; set; }
    public Address Address { get; set; }
}

public class Address
{
    public string Street { get; set; }
    public string City { get; set; }
    public string State { get; set; }
    public string Zip { get; set; }
}

public class RegexGenerator
{
    private static readonly ConditionalWeakTable<Type, CoreRegexCacheEntry> CoreRegexCache = new();

    public static void Test()
    {
        Person person = new Person
        {
            Name = "John Doe",
            Age = 30,
            Address = new Address
            {
                Street = "123 Main St",
                City = "Anytown",
                State = "CA",
                Zip = "12345"
            }
        };

        List<Person> persons = new List<Person>() { person };
        List<Person> longerList = new List<Person>() { person, person, person, person, person, person };


        string json = JsonConvert.SerializeObject(longerList);
        string input = $"Reasoning: I wonder if this works!\nOutput: {json}";

        bool matches1 = RegexGenerator.MatchesSchema(input, persons, true);
        bool matches2 = RegexGenerator.MatchesSchema(input, longerList, true);
        bool matches3 = RegexGenerator.MatchesSchema(input + "broken", person, true);

        Util.Log($"String:\n{input}\n\nShould be true: {matches1}, should be true: {matches2}, should be false: {matches3}");
        
        string regex = FromObject(persons, true);
        Util.Log($"Regex: {regex}");
    }
    
    public static string FromObject(Type type, bool chainOfThought, string? stopToken = null)
    {
        return GenerateRegex(type, chainOfThought, stopToken);
    }

    public static string FromObject(object obj, bool chainOfThought, string? stopToken = null)
    {
        return GenerateRegex(obj.GetType(), chainOfThought, stopToken);
    }
    
    public static bool MatchesSchema(string input, object obj, bool chainOfThought)
    {
        return Regex.IsMatch(input, GenerateRegex(obj.GetType(), chainOfThought));
    }

    private static string GenerateRegex(Type type, bool chainOfThought, string? stopToken = null)
    {
        StringBuilder regexBuilder = new StringBuilder();
        if (chainOfThought)
        {
            //regexBuilder.Append(@"^Reasoning:\s*(.*)\nOutput:\s*");
            regexBuilder.Append(@"Reasoning:\s*(.*)\nOutput:\s*");
        }

        regexBuilder.Append(GetCoreRegex(type));

        if (!string.IsNullOrEmpty(stopToken))
        {
            //regexBuilder.Append(Regex.Escape(stopToken) + "$");
            regexBuilder.Append(Regex.Escape(stopToken));
        }
        /*else
        {
            regexBuilder.Append("$"); // Default to the anchor if no stop token is provided
        }*/

        return regexBuilder.ToString();
    }

    private static string GetCoreRegex(Type type)
    {
        CoreRegexCacheEntry cached = CoreRegexCache.GetValue(
            type,
            static _ => new CoreRegexCacheEntry());
        return cached.GetOrCreate(type);
    }

    private static string GenerateCoreRegex(Type type)
    {
        RegexSchemaShape shape;
        if (RequiresCollectibleSafePath(type))
        {
            // Serializer and framework metadata caches can retain collectible Type metadata.
            // Build the same neutral shape directly from reflection for unloadable plugin types.
            shape = NewtonsoftReflectionRegexSchemaBuilder.Create(type);
        }
        else
        {
            shape = NewtonsoftContractRegexSchemaBuilder.Create(type);
        }

        StringBuilder regexBuilder = new StringBuilder();
        AppendRegexForSchemaType(shape, regexBuilder);
        return regexBuilder.ToString();
    }

    private static bool RequiresCollectibleSafePath(Type type)
    {
        if (type.Assembly.IsCollectible)
        {
            return true;
        }

        Type? elementType = type.GetElementType();
        if (elementType != null && RequiresCollectibleSafePath(elementType))
        {
            return true;
        }

        foreach (Type genericArgument in type.GetGenericArguments())
        {
            if (RequiresCollectibleSafePath(genericArgument))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A weak-table value that never stores or captures its Type key. This permits collectible
    /// AssemblyLoadContexts to unload while still caching their completed regex during use.
    /// </summary>
    private sealed class CoreRegexCacheEntry
    {
        private readonly object _gate = new();
        private string? _regex;

        internal string GetOrCreate(Type type)
        {
            string? regex = Volatile.Read(ref _regex);
            if (regex != null)
            {
                return regex;
            }

            lock (_gate)
            {
                regex = _regex;
                if (regex == null)
                {
                    regex = GenerateCoreRegex(type);
                    Volatile.Write(ref _regex, regex);
                }
            }

            return regex;
        }
    }

    private static void AppendRegexForSchemaType(RegexSchemaShape schema, StringBuilder regexBuilder)
    {
        StringBuilder typeRegexBuilder = new StringBuilder();

        // Construct regex patterns for each applicable type
        if (schema.Types.HasFlag(RegexSchemaTypes.Object))
        {
            StringBuilder objectRegexBuilder = new StringBuilder("\\{\\s*");  // Start object with optional whitespace
            bool first = true;
            foreach (var property in schema.Properties)
            {
                if (!first)
                {
                    objectRegexBuilder.Append(",\\s*"); // Commas between properties, optional whitespace
                }
                objectRegexBuilder.Append("\"");
                objectRegexBuilder.Append(Regex.Escape(property.Name)); // Escape property name
                objectRegexBuilder.Append("\"\\s*:\\s*");
                AppendRegexForSchemaType(property.Shape, objectRegexBuilder); // Recursive call for nested properties
                first = false;
            }
            objectRegexBuilder.Append("\\s*\\}");  // End object with optional whitespace
            AppendOr(typeRegexBuilder, objectRegexBuilder.ToString());
        }

        if (schema.Types.HasFlag(RegexSchemaTypes.Array))
        {
            StringBuilder arrayRegexBuilder = new StringBuilder("\\[\\s*"); // Start array with optional whitespace
            if (schema.Item != null)
            {
                AppendRegexForSchemaType(schema.Item, arrayRegexBuilder); // Regex for first item type
                arrayRegexBuilder.Append("(?:\\s*,\\s*");
                AppendRegexForSchemaType(schema.Item, arrayRegexBuilder); // Regex for subsequent item types
                arrayRegexBuilder.Append(")*?");
            }
            arrayRegexBuilder.Append("\\s*\\]"); // End array with optional whitespace
            AppendOr(typeRegexBuilder, arrayRegexBuilder.ToString());
        }

        // Include regex patterns for other types
        AddSimpleTypeRegex(typeRegexBuilder, schema, RegexSchemaTypes.String, "\"[^\"]*\"");
        AddSimpleTypeRegex(typeRegexBuilder, schema, RegexSchemaTypes.Integer, "\\d+");
        AddSimpleTypeRegex(typeRegexBuilder, schema, RegexSchemaTypes.Number, "-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?");
        AddSimpleTypeRegex(typeRegexBuilder, schema, RegexSchemaTypes.Boolean, "(true|false)");
        AddSimpleTypeRegex(typeRegexBuilder, schema, RegexSchemaTypes.Null, "null");

        regexBuilder.Append("(?:");
        regexBuilder.Append(typeRegexBuilder);
        regexBuilder.Append(")");
    }

    private static void AppendOr(StringBuilder builder, string pattern)
    {
        if (builder.Length > 0)
        {
            builder.Append("|");
        }
        builder.Append(pattern);
    }

    private static void AddSimpleTypeRegex(
        StringBuilder builder,
        RegexSchemaShape schema,
        RegexSchemaTypes type,
        string regexPattern)
    {
        if (schema.Types.HasFlag(type))
        {
            AppendOr(builder, regexPattern);
        }
    }
}

[Flags]
internal enum RegexSchemaTypes
{
    None = 0,
    Object = 1 << 0,
    Array = 1 << 1,
    String = 1 << 2,
    Integer = 1 << 3,
    Number = 1 << 4,
    Boolean = 1 << 5,
    Null = 1 << 6,
}

internal sealed record RegexSchemaProperty(string Name, RegexSchemaShape Shape);

/// <summary>
/// The small, serializer-independent portion of JSON Schema consumed by the legacy regex renderer.
/// </summary>
internal sealed class RegexSchemaShape
{
    internal RegexSchemaShape(
        RegexSchemaTypes types,
        IReadOnlyList<RegexSchemaProperty>? properties = null,
        RegexSchemaShape? item = null)
    {
        Types = types;
        Properties = properties ?? Array.Empty<RegexSchemaProperty>();
        Item = item;
    }

    internal RegexSchemaTypes Types { get; }

    internal IReadOnlyList<RegexSchemaProperty> Properties { get; }

    internal RegexSchemaShape? Item { get; }
}

/// <summary>
/// Builds the neutral regex schema without serializer contract caches for collectible plugin
/// types, allowing their AssemblyLoadContext to unload after the weak cache key is collected.
/// </summary>
internal static class NewtonsoftReflectionRegexSchemaBuilder
{
    internal static RegexSchemaShape Create(Type type)
    {
        return Create(type, new HashSet<Type>());
    }

    private static RegexSchemaShape Create(
        Type type,
        HashSet<Type> activeTypes)
    {
        Type schemaType = Nullable.GetUnderlyingType(type) ?? type;
        if (schemaType.IsByRef && ContainsCollectibleMetadata(schemaType))
        {
            // Json.NET retains ref-return members and resolves a collectible by-ref contract
            // through its element type. Avoid its Type-retaining resolver while preserving
            // that behavior for plugin-defined structs/classes.
            return Create(schemaType.GetElementType()!, activeTypes);
        }

        if (IsKeyValuePair(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (schemaType.IsEnum)
        {
            // StringEnumGenerationProvider wins over a type-level converter in the legacy path.
            return new RegexSchemaShape(RegexSchemaTypes.String);
        }

        if (HasWritableNewtonsoftConverter(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (HasCollectibleSafeInternalConverter(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (schemaType == typeof(object))
        {
            return new RegexSchemaShape(
                RegexSchemaTypes.Object
                | RegexSchemaTypes.Array
                | RegexSchemaTypes.String
                | RegexSchemaTypes.Integer
                | RegexSchemaTypes.Number
                | RegexSchemaTypes.Boolean);
        }

        if (IsStringType(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.String);
        }

        RegexSchemaTypes? primitiveType = GetPrimitiveType(schemaType);
        if (primitiveType != null)
        {
            return new RegexSchemaShape(primitiveType.Value);
        }

        if (!ContainsCollectibleMetadata(schemaType))
        {
            // Resolving a contract is safe when the complete declared type graph is
            // noncollectible. This preserves Json.NET's built-in InternalConverter and
            // TypeConverter behavior without exposing the plugin Type to its caches.
            return NewtonsoftContractRegexSchemaBuilder.Create(schemaType);
        }

        JsonContainerAttribute? containerAttribute =
            GetEffectiveTypeAttribute<JsonContainerAttribute>(schemaType);
        if (containerAttribute is JsonObjectAttribute)
        {
            return CreateObject(schemaType, activeTypes);
        }

        if (containerAttribute is JsonArrayAttribute)
        {
            return CreateArray(schemaType, GetEnumerableElementType(schemaType), activeTypes);
        }

        if (containerAttribute is JsonDictionaryAttribute)
        {
            if (!IsDictionaryType(schemaType))
            {
                throw new Exception($"Type {schemaType} is not a dictionary.");
            }

            return new RegexSchemaShape(RegexSchemaTypes.Object);
        }

        if (typeof(Newtonsoft.Json.Linq.JToken).IsAssignableFrom(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (IsDictionaryType(schemaType))
        {
            // The legacy renderer ignores additionalProperties, so dictionaries render
            // as an empty object even when their values have a concrete schema.
            return new RegexSchemaShape(RegexSchemaTypes.Object);
        }

        Type? elementType = GetEnumerableElementType(schemaType);
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(schemaType))
        {
            return CreateArray(schemaType, elementType, activeTypes);
        }

        if (HasStringTypeConverter(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.String);
        }

        if (UsesNewtonsoftISerializableContract(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.Object);
        }

        if (typeof(System.Dynamic.IDynamicMetaObjectProvider).IsAssignableFrom(schemaType))
        {
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (typeof(IConvertible).IsAssignableFrom(schemaType))
        {
            return AnyNonNullValue();
        }

        return CreateObject(schemaType, activeTypes);
    }

    private static RegexSchemaShape CreateArray(
        Type type,
        Type? elementType,
        HashSet<Type> activeTypes)
    {
        Enter(type, activeTypes);
        try
        {
            RegexSchemaShape? item = elementType == null
                ? null
                : Create(elementType, activeTypes);
            return new RegexSchemaShape(RegexSchemaTypes.Array, item: item);
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static RegexSchemaShape CreateObject(
        Type type,
        HashSet<Type> activeTypes)
    {
        if (type.ContainsGenericParameters)
        {
            // Json.NET attempts to create a DynamicMethod owned by an open object-contract
            // type before it discovers members, including extension-data members.
            throw new ArgumentException("Invalid type owner for DynamicMethod.");
        }

        Enter(type, activeTypes);
        try
        {
            List<SerializableMember> members = GetSerializableMembers(type);
            MemberInfo? extensionDataMember = ValidateExtensionDataMembers(type);
            ValidateExtensionDataConstructor(extensionDataMember);
            var properties = new List<RegexSchemaProperty>(members.Count);
            foreach (SerializableMember member in members)
            {
                RegexSchemaShape propertyShape = Create(member.ValueType, activeTypes);
                if (member.AllowsNull)
                {
                    propertyShape = new RegexSchemaShape(
                        propertyShape.Types | RegexSchemaTypes.Null,
                        propertyShape.Properties,
                        propertyShape.Item);
                }

                properties.Add(new RegexSchemaProperty(member.Name, propertyShape));
            }

            return new RegexSchemaShape(RegexSchemaTypes.Object, properties);
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static void Enter(Type type, HashSet<Type> activeTypes)
    {
        if (!activeTypes.Add(type))
        {
            throw new NotSupportedException(
                "RegexGenerator does not support recursive or circular schemas.");
        }
    }

    private static List<SerializableMember> GetSerializableMembers(Type type)
    {
        JsonObjectAttribute? objectAttribute =
            GetEffectiveTypeAttribute<JsonContainerAttribute>(type) as JsonObjectAttribute;
        bool dataContract = UsesDataContract(type);
        MemberSerialization serialization = objectAttribute?.MemberSerialization
                                            ?? (dataContract
                                                ? MemberSerialization.OptIn
                                                : MemberSerialization.OptOut);

        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        var result = new List<SerializableMember>();
        var names = new Dictionary<string, Type>(StringComparer.Ordinal);
        var containerNamingStrategies = new Dictionary<Type, NamingStrategy?>();
        int sequence = 0;

        List<Type> hierarchy = EnumerateContractHierarchy(type).ToList();
        IEnumerable<MemberInfo> declaredMembers = hierarchy
            .SelectMany(current => current.GetFields(flags).Cast<MemberInfo>())
            .Concat(EnumerateEffectiveProperties(hierarchy, flags));
        foreach (MemberInfo member in declaredMembers)
        {
            Type? valueType = member switch
            {
                PropertyInfo property when property.GetIndexParameters().Length == 0
                    => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => null,
            };
            if (valueType == null
                || valueType.IsByRefLike
                || IsNewtonsoftExcludedExceptionMember(type, member)
                || (serialization != MemberSerialization.Fields
                    && member.GetCustomAttribute<CompilerGeneratedAttribute>(inherit: true) != null))
            {
                continue;
            }

            IReadOnlyList<MemberInfo> metadataSources = GetMemberMetadataSources(member);
            var jsonPropertyMetadata = FindEffectiveAttribute<JsonPropertyAttribute>(metadataSources);
            JsonPropertyAttribute? jsonProperty = jsonPropertyMetadata.Value;
            var jsonRequiredMetadata =
                FindEffectiveAttribute<Newtonsoft.Json.JsonRequiredAttribute>(metadataSources);
            Newtonsoft.Json.JsonRequiredAttribute? jsonRequired = jsonRequiredMetadata.Value;
            DataMemberAttribute? dataMember = dataContract
                ? FindEffectiveAttribute<DataMemberAttribute>(metadataSources).Value
                : null;
            Newtonsoft.Json.JsonExtensionDataAttribute? extensionData =
                FindEffectiveAttribute<Newtonsoft.Json.JsonExtensionDataAttribute>(
                    metadataSources).Value;
            bool dataAnnotationsRequired = metadataSources.Any(source =>
                (type.IsInterface || source.DeclaringType?.IsInterface != true)
                && source.GetCustomAttribute<
                    System.ComponentModel.DataAnnotations.RequiredAttribute>(
                    inherit: false) != null);
            bool explicitlyIncluded = jsonProperty != null
                                      || jsonRequired != null
                                      || dataMember != null;
            bool ignored = FindEffectiveAttribute<Newtonsoft.Json.JsonIgnoreAttribute>(
                               metadataSources).Value != null
                           || extensionData != null
                           || (serialization != MemberSerialization.OptIn
                               && FindEffectiveAttribute<IgnoreDataMemberAttribute>(
                                   metadataSources).Value != null)
                           || member.GetCustomAttribute<NonSerializedAttribute>(
                               inherit: true) != null;
            bool included = serialization switch
            {
                MemberSerialization.OptIn => explicitlyIncluded,
                MemberSerialization.Fields => member is FieldInfo field && !field.IsStatic,
                _ => IsPubliclySerializable(member) || explicitlyIncluded,
            };
            if (ignored || !included)
            {
                continue;
            }

            string mappedName = jsonProperty?.PropertyName
                                ?? dataMember?.Name
                                ?? member.Name;
            bool hasSpecifiedName = jsonProperty?.PropertyName != null
                                    || dataMember?.Name != null;
            NamingStrategy? memberNamingStrategy = CreateNamingStrategy(
                jsonProperty?.NamingStrategyType,
                jsonProperty?.NamingStrategyParameters);
            Type declaringType = member.DeclaringType ?? type;
            NamingStrategy? objectNamingStrategy = GetContainerNamingStrategy(
                declaringType,
                containerNamingStrategies);
            string name = (memberNamingStrategy ?? objectNamingStrategy)?.GetPropertyName(
                              mappedName,
                              hasSpecifiedName)
                          ?? mappedName;
            if (names.TryGetValue(name, out Type? existingDeclaringType))
            {
                if (existingDeclaringType == declaringType)
                {
                    throw new JsonSerializationException(
                        $"A member with the name '{name}' already exists on '{type}'. " +
                        "Use the JsonPropertyAttribute to specify another name.");
                }

                // The closest declaration wins for an override, hidden member, or base collision.
                continue;
            }

            names.Add(name, declaringType);

            int order = -1;
            if (jsonProperty != null)
            {
                if (IsJsonPropertyArgumentSpecified(
                        jsonPropertyMetadata.Source,
                        nameof(JsonPropertyAttribute.Order)))
                {
                    order = jsonProperty.Order;
                }
            }
            else if (dataMember is { Order: >= 0 })
            {
                order = dataMember.Order;
            }

            result.Add(new SerializableMember(
                valueType,
                name,
                order,
                sequence++,
                AllowsNull(
                    valueType,
                    jsonPropertyMetadata.Source,
                    jsonProperty,
                    jsonRequired,
                    dataMember,
                    dataAnnotationsRequired)));
        }

        return result
            .OrderBy(member => member.Order)
            .ThenBy(member => member.Sequence)
            .ToList();
    }

    private static (TAttribute? Value, MemberInfo? Source) FindEffectiveAttribute<TAttribute>(
        IReadOnlyList<MemberInfo> sources)
        where TAttribute : Attribute
    {
        foreach (MemberInfo source in sources)
        {
            TAttribute? attribute = source.GetCustomAttribute<TAttribute>(inherit: false);
            if (attribute != null)
            {
                return (attribute, source);
            }
        }

        return (null, null);
    }

    private static IReadOnlyList<MemberInfo> GetMemberMetadataSources(MemberInfo member)
    {
        var sources = new List<MemberInfo>();
        AddMemberAndMetadataSources(sources, member);
        if (member is not PropertyInfo property)
        {
            return sources;
        }

        MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
        if (accessor?.IsVirtual == true)
        {
            MethodInfo baseDefinition = accessor.GetBaseDefinition();
            if (baseDefinition != accessor)
            {
                const BindingFlags propertyFlags = BindingFlags.Instance
                                                   | BindingFlags.Public
                                                   | BindingFlags.NonPublic
                                                   | BindingFlags.DeclaredOnly;
                for (Type? current = property.DeclaringType?.BaseType;
                     current != null;
                     current = current.BaseType)
                {
                    foreach (PropertyInfo candidate in current.GetProperties(propertyFlags))
                    {
                        MethodInfo? candidateAccessor = candidate.GetMethod ?? candidate.SetMethod;
                        if (candidateAccessor?.GetBaseDefinition() == baseDefinition
                            && candidate.PropertyType == property.PropertyType)
                        {
                            AddMemberAndMetadataSources(sources, candidate);
                        }
                    }
                }
            }
        }

        Type? declaringType = member.DeclaringType;
        if (declaringType == null)
        {
            return sources;
        }

        // JsonTypeReflector maps attributes by same member name/signature on interfaces of
        // the physical declaring type. It does not use a root-type interface map: an interface
        // newly added by a derived class therefore cannot annotate an inherited base member,
        // and an explicit implementation's qualified CLR name does not match "Value".
        foreach (Type interfaceType in declaringType.GetInterfaces())
        {
            MemberInfo? interfaceMember = FindCorrespondingMember(interfaceType, member);
            if (interfaceMember != null)
            {
                AddMemberAndMetadataSources(sources, interfaceMember);
            }
        }

        return sources;
    }

    private static void AddMemberAndMetadataSources(
        ICollection<MemberInfo> sources,
        MemberInfo member)
    {
        Type? metadataType = member.DeclaringType == null
            ? null
            : GetAssociatedMetadataType(member.DeclaringType);
        MemberInfo? metadataMember = metadataType == null
            ? null
            : FindCorrespondingMember(metadataType, member);
        if (metadataMember != null && !sources.Contains(metadataMember))
        {
            sources.Add(metadataMember);
        }

        if (!sources.Contains(member))
        {
            sources.Add(member);
        }
    }

    private static MemberInfo? FindCorrespondingMember(Type targetType, MemberInfo member)
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.FlattenHierarchy;
        if (member is FieldInfo field)
        {
            return targetType.GetFields(flags).FirstOrDefault(candidate =>
                candidate.Name == field.Name && candidate.FieldType == field.FieldType);
        }

        if (member is PropertyInfo property)
        {
            Type[] indexTypes = property.GetIndexParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray();
            foreach (PropertyInfo candidate in targetType.GetProperties(flags))
            {
                if (candidate.Name != property.Name
                    || candidate.PropertyType != property.PropertyType)
                {
                    continue;
                }

                ParameterInfo[] candidateIndexes = candidate.GetIndexParameters();
                if (candidateIndexes.Length == indexTypes.Length
                    && candidateIndexes.Select(parameter => parameter.ParameterType)
                        .SequenceEqual(indexTypes))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static JsonObjectAttribute? GetJsonObjectAttribute(Type type)
    {
        return GetEffectiveTypeAttribute<JsonContainerAttribute>(type) as JsonObjectAttribute;
    }

    private static TAttribute? GetEffectiveTypeAttribute<TAttribute>(Type type)
        where TAttribute : Attribute
    {
        Type? metadataType = GetAssociatedMetadataType(type);
        TAttribute? attribute = metadataType?.GetCustomAttribute<TAttribute>(inherit: true);
        if (attribute != null)
        {
            return attribute;
        }

        attribute = type.GetCustomAttribute<TAttribute>(inherit: true);
        if (attribute != null)
        {
            return attribute;
        }

        foreach (Type interfaceType in type.GetInterfaces())
        {
            metadataType = GetAssociatedMetadataType(interfaceType);
            attribute = metadataType?.GetCustomAttribute<TAttribute>(inherit: true)
                        ?? interfaceType.GetCustomAttribute<TAttribute>(inherit: true);
            if (attribute != null)
            {
                return attribute;
            }
        }

        return null;
    }

    private static Type? GetAssociatedMetadataType(Type type)
    {
        return type.GetCustomAttribute<
            System.ComponentModel.DataAnnotations.MetadataTypeAttribute>(inherit: true)
            ?.MetadataClassType;
    }

    private static NamingStrategy? GetContainerNamingStrategy(
        Type declaringType,
        IDictionary<Type, NamingStrategy?> cache)
    {
        if (cache.TryGetValue(declaringType, out NamingStrategy? strategy))
        {
            return strategy;
        }

        JsonObjectAttribute? attribute = GetJsonObjectAttribute(declaringType);
        strategy = CreateNamingStrategy(
            attribute?.NamingStrategyType,
            attribute?.NamingStrategyParameters);
        cache.Add(declaringType, strategy);
        return strategy;
    }

    private static bool IsPubliclySerializable(MemberInfo member)
    {
        return member switch
        {
            PropertyInfo property => (property.GetMethod?.IsPublic == true
                                      && property.GetMethod.IsStatic == false)
                                     || (property.SetMethod?.IsPublic == true
                                         && property.SetMethod.IsStatic == false),
            FieldInfo field => field.IsPublic && !field.IsStatic,
            _ => false,
        };
    }

    private static IEnumerable<Type> EnumerateContractHierarchy(Type type)
    {
        for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            yield return current;
        }

        if (!type.IsInterface)
        {
            yield break;
        }

        foreach (Type inheritedInterface in type.GetInterfaces())
        {
            yield return inheritedInterface;
        }
    }

    private static IEnumerable<PropertyInfo> EnumerateEffectiveProperties(
        IReadOnlyList<Type> hierarchy,
        BindingFlags flags)
    {
        // Json.NET collapses ordinary virtual overrides before applying JSON names. If the
        // names differ, name-based collision handling is too late and would emit both slots.
        // Property type remains part of its identity, matching Json.NET's treatment of a
        // covariant-return override as a distinct contract property.
        var virtualSlots = new HashSet<(MethodInfo BaseDefinition, Type PropertyType)>();
        foreach (Type current in hierarchy)
        {
            foreach (PropertyInfo property in current.GetProperties(flags))
            {
                MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
                if (accessor?.IsVirtual == true
                    && !virtualSlots.Add((accessor.GetBaseDefinition(), property.PropertyType)))
                {
                    continue;
                }

                yield return property;
            }
        }
    }

    private static bool IsNewtonsoftExcludedExceptionMember(Type objectType, MemberInfo member)
    {
        // Json.NET's object contract for a custom Exception subclass excludes TargetSite.
        // Following that MethodBase property would both change the legacy grammar and introduce
        // a reflection-type cycle in the collectible, resolver-free path.
        return typeof(Exception).IsAssignableFrom(objectType)
               && member.Name == nameof(Exception.TargetSite);
    }

    private static bool AllowsNull(
        Type valueType,
        MemberInfo? jsonPropertySource,
        JsonPropertyAttribute? jsonProperty,
        Newtonsoft.Json.JsonRequiredAttribute? jsonRequired,
        DataMemberAttribute? dataMember,
        bool dataAnnotationsRequired)
    {
        if (dataAnnotationsRequired
            || jsonRequired != null
            || jsonProperty?.Required is Required.Always or Required.DisallowNull)
        {
            return false;
        }

        bool explicitlyDefault = jsonProperty?.Required == Required.Default
                                 && IsJsonPropertyArgumentSpecified(
                                     jsonPropertySource,
                                     nameof(JsonPropertyAttribute.Required));
        bool contractAllowsNull = jsonProperty != null
            ? jsonProperty.Required == Required.AllowNull || explicitlyDefault
            : dataMember != null;
        return contractAllowsNull && CanRepresentNull(valueType);
    }

    private static bool IsJsonPropertyArgumentSpecified(
        MemberInfo? member,
        string argumentName)
    {
        return member?.CustomAttributes.Any(attribute =>
                   attribute.AttributeType == typeof(JsonPropertyAttribute)
                   && attribute.NamedArguments.Any(argument =>
                       argument.MemberName == argumentName)) == true;
    }

    private static bool UsesDataContract(Type type)
    {
        // DataContractAttribute itself is not inherited, but JsonTypeReflector deliberately
        // checks the full class hierarchy (with metadata/interface lookup at each level).
        for (Type? current = type; current != null; current = current.BaseType)
        {
            if (GetEffectiveTypeAttribute<DataContractAttribute>(current) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanRepresentNull(Type type)
    {
        return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
    }

    private static RegexSchemaShape AnyNonNullValue()
    {
        return new RegexSchemaShape(
            RegexSchemaTypes.Object
            | RegexSchemaTypes.Array
            | RegexSchemaTypes.String
            | RegexSchemaTypes.Integer
            | RegexSchemaTypes.Number
            | RegexSchemaTypes.Boolean);
    }

    private static NamingStrategy? CreateNamingStrategy(
        Type? namingStrategyType,
        object[]? parameters)
    {
        if (namingStrategyType == null)
        {
            return null;
        }

        if (!typeof(NamingStrategy).IsAssignableFrom(namingStrategyType))
        {
            throw new InvalidOperationException(
                $"Naming strategy type '{namingStrategyType}' does not derive from " +
                $"{nameof(NamingStrategy)}.");
        }

        object? instance = Activator.CreateInstance(
            namingStrategyType,
            parameters ?? Array.Empty<object>());
        return instance as NamingStrategy
               ?? throw new InvalidOperationException(
                   $"Could not create naming strategy '{namingStrategyType}'.");
    }

    private static bool HasWritableNewtonsoftConverter(Type type)
    {
        Newtonsoft.Json.JsonConverterAttribute? attribute =
            GetEffectiveTypeAttribute<Newtonsoft.Json.JsonConverterAttribute>(type);
        if (attribute == null)
        {
            return false;
        }

        object? converter = Activator.CreateInstance(
            attribute.ConverterType,
            attribute.ConverterParameters ?? Array.Empty<object>());
        return converter is Newtonsoft.Json.JsonConverter jsonConverter
               && jsonConverter.CanWrite;
    }

    private static bool HasCollectibleSafeInternalConverter(Type type)
    {
        // Json.NET's built-in converters use assignability checks for these BCL families.
        // Keep this explicit and cache-free: resolving a collectible Type through
        // DefaultContractResolver would root its AssemblyLoadContext.
        return typeof(System.Data.DataTable).IsAssignableFrom(type)
               || typeof(System.Data.DataSet).IsAssignableFrom(type)
               || typeof(System.Xml.XmlNode).IsAssignableFrom(type)
               || typeof(System.Xml.Linq.XObject).IsAssignableFrom(type);
    }

    private static bool HasStringTypeConverter(Type type)
    {
        TypeConverterAttribute? attribute = type.GetCustomAttribute<TypeConverterAttribute>(
            inherit: true);
        string? converterTypeName = attribute?.ConverterTypeName;
        if (string.IsNullOrWhiteSpace(converterTypeName))
        {
            return false;
        }

        string localTypeName = converterTypeName.Split(',')[0].Trim();
        Type? converterType = type.Assembly.GetType(
                                  localTypeName,
                                  throwOnError: false,
                                  ignoreCase: false)
                              ?? System.Type.GetType(converterTypeName, throwOnError: false);
        if (converterType == null)
        {
            return false;
        }

        object? converter = Activator.CreateInstance(converterType);
        return converter is TypeConverter typeConverter
               && typeConverter.CanConvertTo(typeof(string));
    }

    private static bool ContainsCollectibleMetadata(Type type)
    {
        if (type.Assembly.IsCollectible)
        {
            return true;
        }

        Type? elementType = type.GetElementType();
        if (elementType != null && ContainsCollectibleMetadata(elementType))
        {
            return true;
        }

        foreach (Type genericArgument in type.GetGenericArguments())
        {
            if (ContainsCollectibleMetadata(genericArgument))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStringType(Type type)
    {
        return type == typeof(string)
               || type == typeof(char)
               || type == typeof(Guid)
               || type == typeof(DateTime)
               || type == typeof(DateTimeOffset)
               || type == typeof(DateOnly)
               || type == typeof(TimeOnly)
               || type == typeof(TimeSpan)
               || type == typeof(Uri)
               || type == typeof(Version)
               || type == typeof(CultureInfo)
               || type == typeof(byte[]);
    }

    private static RegexSchemaTypes? GetPrimitiveType(Type type)
    {
        if (type == typeof(System.Numerics.BigInteger))
        {
            return RegexSchemaTypes.Integer;
        }

        if (type == typeof(DBNull))
        {
            return RegexSchemaTypes.Null;
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => RegexSchemaTypes.Boolean,
            TypeCode.SByte or TypeCode.Byte
                or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 => RegexSchemaTypes.Integer,
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => RegexSchemaTypes.Number,
            _ => null,
        };
    }

    private static bool IsDictionaryType(Type type)
    {
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            return true;
        }

        foreach (Type candidate in type.GetInterfaces().Prepend(type))
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            Type definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>)
                || definition == typeof(IReadOnlyDictionary<,>))
            {
                return true;
            }
        }

        return false;
    }

    private static MemberInfo? ValidateExtensionDataMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        IEnumerable<Type> hierarchy;
        if (type.IsInterface)
        {
            hierarchy = new[] { type };
        }
        else
        {
            var classes = new Stack<Type>();
            for (Type? current = type;
                 current != null && current != typeof(object);
                 current = current.BaseType)
            {
                classes.Push(current);
            }

            hierarchy = classes;
        }

        IEnumerable<MemberInfo> candidates = hierarchy
            .SelectMany(current => current.GetProperties(flags).Cast<MemberInfo>()
                .Concat(current.GetFields(flags)));
        MemberInfo? extensionDataMember = null;
        foreach (MemberInfo candidate in candidates)
        {
            if (!candidate.IsDefined(
                    typeof(Newtonsoft.Json.JsonExtensionDataAttribute),
                    inherit: false))
            {
                continue;
            }

            Type declaringType = candidate.DeclaringType ?? type;
            if (candidate is PropertyInfo { GetMethod: null })
            {
                throw new JsonException(
                    $"Invalid extension data attribute on '{GetClrTypeFullName(declaringType)}'. " +
                    $"Member '{candidate.Name}' must have a getter.");
            }

            Type valueType = candidate switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => typeof(void),
            };
            if (!IsValidExtensionDataType(valueType))
            {
                throw new JsonException(
                    $"Invalid extension data attribute on '{GetClrTypeFullName(declaringType)}'. " +
                    $"Member '{candidate.Name}' type must implement " +
                    "IDictionary<string, JToken>.");
            }

            extensionDataMember = candidate;
        }

        return extensionDataMember;
    }

    private static bool IsValidExtensionDataType(Type type)
    {
        Type? dictionaryType = null;
        if (type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IDictionary<,>))
        {
            dictionaryType = type;
        }
        else
        {
            dictionaryType = type.GetInterfaces().FirstOrDefault(candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }

        if (dictionaryType == null)
        {
            return false;
        }

        Type[] arguments = dictionaryType.GetGenericArguments();
        return arguments[0].IsAssignableFrom(typeof(string))
               && arguments[1].IsAssignableFrom(typeof(Newtonsoft.Json.Linq.JToken));
    }

    private static void ValidateExtensionDataConstructor(MemberInfo? member)
    {
        if (member == null)
        {
            return;
        }

        Newtonsoft.Json.JsonExtensionDataAttribute? extensionData =
            FindEffectiveAttribute<Newtonsoft.Json.JsonExtensionDataAttribute>(
                GetMemberMetadataSources(member)).Value;
        if (extensionData?.ReadData != true)
        {
            return;
        }

        Type memberType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(void),
        };
        Type createdType = memberType;
        if (memberType.IsGenericType
            && memberType.GetGenericTypeDefinition() == typeof(IDictionary<,>))
        {
            createdType = typeof(Dictionary<,>).MakeGenericType(
                memberType.GetGenericArguments());
        }

        if (createdType.IsValueType)
        {
            return;
        }

        ConstructorInfo? constructor = createdType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (constructor == null)
        {
            throw new ArgumentException($"Could not get constructor for {createdType}.");
        }
    }

    private static string GetClrTypeFullName(Type type)
    {
        if (type.IsGenericTypeDefinition || !type.ContainsGenericParameters)
        {
            return type.FullName ?? type.Name;
        }

        return string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : type.Namespace + "." + type.Name;
    }

    private static bool IsKeyValuePair(Type type)
    {
        return type.IsGenericType
               && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
    }

    private static bool UsesNewtonsoftISerializableContract(Type type)
    {
        return typeof(ISerializable).IsAssignableFrom(type)
               && type.GetCustomAttribute<SerializableAttribute>(inherit: false) != null
               && GetJsonObjectAttribute(type) == null
               && type.GetCustomAttribute<JsonArrayAttribute>(inherit: true) == null
               && type.GetCustomAttribute<JsonDictionaryAttribute>(inherit: true) == null;
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        foreach (Type candidate in type.GetInterfaces().Prepend(type))
        {
            if (candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private sealed record SerializableMember(
        Type ValueType,
        string Name,
        int Order,
        int Sequence,
        bool AllowsNull);
}

/// <summary>
/// Builds the neutral regex schema from Json.NET's resolved contracts. System.Text.Json
/// attributes never participate, matching the legacy Newtonsoft.Json.Schema generator.
/// </summary>
internal static class NewtonsoftContractRegexSchemaBuilder
{
    private static readonly PropertyInfo InternalConverterProperty =
        typeof(JsonContract).GetProperty(
            "InternalConverter",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(
            typeof(JsonContract).FullName,
            "InternalConverter");
    private static readonly PropertyInfo IsRequiredSpecifiedProperty =
        typeof(Newtonsoft.Json.Serialization.JsonProperty).GetProperty(
            "IsRequiredSpecified",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(
            typeof(Newtonsoft.Json.Serialization.JsonProperty).FullName,
            "IsRequiredSpecified");

    internal static RegexSchemaShape Create(Type type)
    {
        // Keep the resolver local: DefaultContractResolver caches every Type it sees.
        var resolver = new DefaultContractResolver();
        return Create(type, resolver, new HashSet<Type>());
    }

    private static RegexSchemaShape Create(
        Type type,
        DefaultContractResolver resolver,
        HashSet<Type> activeTypes)
    {
        Type schemaType = Nullable.GetUnderlyingType(type) ?? type;
        if (IsKeyValuePair(schemaType))
        {
            // Json.NET's schema generator leaves KeyValuePair contracts untyped.
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (schemaType.IsEnum)
        {
            // StringEnumGenerationProvider wins over a type-level converter in the legacy path.
            return new RegexSchemaShape(RegexSchemaTypes.String);
        }

        JsonContract contract = resolver.ResolveContract(schemaType);
        if (contract.Converter?.CanWrite == true
            || GetInternalConverter(contract)?.CanWrite == true)
        {
            // The legacy generator treats a writable type-level converter as untyped.
            // A read-only converter is ignored. Property and item converters are also ignored.
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (schemaType == typeof(object))
        {
            return AnyNonNullValue();
        }

        if (IsStringType(schemaType) || contract is JsonStringContract)
        {
            return new RegexSchemaShape(RegexSchemaTypes.String);
        }

        RegexSchemaTypes? primitiveType = GetPrimitiveType(schemaType);
        if (primitiveType != null)
        {
            return new RegexSchemaShape(primitiveType.Value);
        }

        if (contract is JsonLinqContract or JsonDynamicContract)
        {
            return new RegexSchemaShape(RegexSchemaTypes.None);
        }

        if (contract is JsonPrimitiveContract)
        {
            return AnyNonNullValue();
        }

        if (contract is JsonDictionaryContract or JsonISerializableContract)
        {
            // The renderer ignores additionalProperties and serialization-info members.
            return new RegexSchemaShape(RegexSchemaTypes.Object);
        }

        if (contract is JsonArrayContract arrayContract)
        {
            return CreateArray(schemaType, arrayContract, resolver, activeTypes);
        }

        if (contract is JsonObjectContract objectContract)
        {
            return CreateObject(schemaType, objectContract, resolver, activeTypes);
        }

        return new RegexSchemaShape(RegexSchemaTypes.None);
    }

    private static RegexSchemaShape CreateArray(
        Type type,
        JsonArrayContract contract,
        DefaultContractResolver resolver,
        HashSet<Type> activeTypes)
    {
        Enter(type, activeTypes);
        try
        {
            RegexSchemaShape? item = contract.CollectionItemType == null
                ? null
                : Create(contract.CollectionItemType, resolver, activeTypes);
            return new RegexSchemaShape(RegexSchemaTypes.Array, item: item);
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static RegexSchemaShape CreateObject(
        Type type,
        JsonObjectContract contract,
        DefaultContractResolver resolver,
        HashSet<Type> activeTypes)
    {
        Enter(type, activeTypes);
        try
        {
            var properties = new List<RegexSchemaProperty>(contract.Properties.Count);
            foreach (Newtonsoft.Json.Serialization.JsonProperty property in contract.Properties)
            {
                if (property.Ignored || property.PropertyType == null)
                {
                    continue;
                }

                RegexSchemaShape propertyShape = Create(
                    property.PropertyType,
                    resolver,
                    activeTypes);
                MemberInfo? member = FindUnderlyingMember(property);
                if (AllowsNull(type, property, member))
                {
                    propertyShape = AddNull(propertyShape);
                }

                string name = property.PropertyName
                              ?? property.UnderlyingName
                              ?? string.Empty;
                properties.Add(new RegexSchemaProperty(name, propertyShape));
            }

            return new RegexSchemaShape(RegexSchemaTypes.Object, properties);
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static void Enter(Type type, HashSet<Type> activeTypes)
    {
        if (!activeTypes.Add(type))
        {
            throw new NotSupportedException(
                "RegexGenerator does not support recursive or circular schemas.");
        }
    }

    private static RegexSchemaShape AddNull(RegexSchemaShape shape)
    {
        return new RegexSchemaShape(
            shape.Types | RegexSchemaTypes.Null,
            shape.Properties,
            shape.Item);
    }

    private static RegexSchemaShape AnyNonNullValue()
    {
        return new RegexSchemaShape(
            RegexSchemaTypes.Object
            | RegexSchemaTypes.Array
            | RegexSchemaTypes.String
            | RegexSchemaTypes.Integer
            | RegexSchemaTypes.Number
            | RegexSchemaTypes.Boolean);
    }

    private static bool AllowsNull(
        Type objectType,
        Newtonsoft.Json.Serialization.JsonProperty property,
        MemberInfo? member)
    {
        if (GetMetadataOrDirectMemberAttribute<
                System.ComponentModel.DataAnnotations.RequiredAttribute>(member) != null
            || property.Required is Required.Always or Required.DisallowNull)
        {
            return false;
        }

        bool explicitlyDefault = property.Required == Required.Default
                                 && IsRequiredSpecified(property);
        bool hasJsonProperty = GetEffectiveMemberAttribute<JsonPropertyAttribute>(member) != null;
        bool contractAllowsNull = property.Required == Required.AllowNull
                                  || explicitlyDefault
                                  || (!hasJsonProperty
                                      && UsesDataContract(objectType)
                                      && GetEffectiveMemberAttribute<DataMemberAttribute>(member)
                                      != null);
        return contractAllowsNull
               && (!property.PropertyType!.IsValueType
                   || Nullable.GetUnderlyingType(property.PropertyType) != null);
    }

    private static Newtonsoft.Json.JsonConverter? GetInternalConverter(JsonContract contract)
    {
        return InternalConverterProperty.GetValue(contract) as Newtonsoft.Json.JsonConverter;
    }

    private static bool IsRequiredSpecified(
        Newtonsoft.Json.Serialization.JsonProperty property)
    {
        return IsRequiredSpecifiedProperty.GetValue(property) is true;
    }

    private static TAttribute? GetMetadataOrDirectMemberAttribute<TAttribute>(
        MemberInfo? member)
        where TAttribute : Attribute
    {
        if (member == null)
        {
            return null;
        }

        Type? metadataType = member.DeclaringType == null
            ? null
            : GetAssociatedMetadataType(member.DeclaringType);
        MemberInfo? metadataMember = metadataType == null
            ? null
            : FindCorrespondingMember(metadataType, member);
        return metadataMember?.GetCustomAttribute<TAttribute>(inherit: true)
               ?? member.GetCustomAttribute<TAttribute>(inherit: true);
    }

    private static TAttribute? GetEffectiveMemberAttribute<TAttribute>(MemberInfo? member)
        where TAttribute : Attribute
    {
        TAttribute? attribute = GetMetadataOrDirectMemberAttribute<TAttribute>(member);
        Type? declaringType = member?.DeclaringType;
        if (attribute != null || member == null || declaringType == null)
        {
            return attribute;
        }

        foreach (Type interfaceType in declaringType.GetInterfaces())
        {
            MemberInfo? interfaceMember = FindCorrespondingMember(interfaceType, member);
            attribute = GetMetadataOrDirectMemberAttribute<TAttribute>(interfaceMember);
            if (attribute != null)
            {
                return attribute;
            }
        }

        return null;
    }

    private static MemberInfo? FindCorrespondingMember(Type targetType, MemberInfo member)
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.FlattenHierarchy;
        if (member is FieldInfo field)
        {
            return targetType.GetFields(flags).FirstOrDefault(candidate =>
                candidate.Name == field.Name && candidate.FieldType == field.FieldType);
        }

        if (member is not PropertyInfo property)
        {
            return null;
        }

        Type[] indexTypes = property.GetIndexParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        return targetType.GetProperties(flags).FirstOrDefault(candidate =>
            candidate.Name == property.Name
            && candidate.PropertyType == property.PropertyType
            && candidate.GetIndexParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(indexTypes));
    }

    private static Type? GetAssociatedMetadataType(Type type)
    {
        return type.GetCustomAttribute<
            System.ComponentModel.DataAnnotations.MetadataTypeAttribute>(inherit: true)
            ?.MetadataClassType;
    }

    private static TAttribute? GetEffectiveTypeAttribute<TAttribute>(Type type)
        where TAttribute : Attribute
    {
        Type? metadataType = GetAssociatedMetadataType(type);
        TAttribute? attribute = metadataType?.GetCustomAttribute<TAttribute>(inherit: true)
                                ?? type.GetCustomAttribute<TAttribute>(inherit: true);
        if (attribute != null)
        {
            return attribute;
        }

        foreach (Type interfaceType in type.GetInterfaces())
        {
            metadataType = GetAssociatedMetadataType(interfaceType);
            attribute = metadataType?.GetCustomAttribute<TAttribute>(inherit: true)
                        ?? interfaceType.GetCustomAttribute<TAttribute>(inherit: true);
            if (attribute != null)
            {
                return attribute;
            }
        }

        return null;
    }

    private static bool UsesDataContract(Type type)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            if (GetEffectiveTypeAttribute<DataContractAttribute>(current) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static MemberInfo? FindUnderlyingMember(
        Newtonsoft.Json.Serialization.JsonProperty property)
    {
        Type? declaringType = property.DeclaringType;
        string? underlyingName = property.UnderlyingName;
        if (declaringType == null || string.IsNullOrEmpty(underlyingName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        for (Type? current = declaringType; current != null; current = current.BaseType)
        {
            PropertyInfo? reflectedProperty = current.GetProperty(underlyingName, flags);
            if (reflectedProperty?.PropertyType == property.PropertyType)
            {
                return reflectedProperty;
            }

            FieldInfo? reflectedField = current.GetField(underlyingName, flags);
            if (reflectedField?.FieldType == property.PropertyType)
            {
                return reflectedField;
            }
        }

        return null;
    }

    private static bool IsKeyValuePair(Type type)
    {
        return type.IsGenericType
               && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
    }

    private static bool IsStringType(Type type)
    {
        return type == typeof(string)
               || type == typeof(char)
               || type == typeof(Guid)
               || type == typeof(DateTime)
               || type == typeof(DateTimeOffset)
               || type == typeof(DateOnly)
               || type == typeof(TimeOnly)
               || type == typeof(TimeSpan)
               || type == typeof(Uri)
               || type == typeof(byte[]);
    }

    private static RegexSchemaTypes? GetPrimitiveType(Type type)
    {
        if (type == typeof(System.Numerics.BigInteger))
        {
            return RegexSchemaTypes.Integer;
        }

        if (type == typeof(DBNull))
        {
            return RegexSchemaTypes.Null;
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => RegexSchemaTypes.Boolean,
            TypeCode.SByte or TypeCode.Byte
                or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 => RegexSchemaTypes.Integer,
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => RegexSchemaTypes.Number,
            _ => null,
        };
    }
}
