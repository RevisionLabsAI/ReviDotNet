// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Schema.Generation;

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
        JSchemaGenerator schemaGenerator = new JSchemaGenerator();
        schemaGenerator.DefaultRequired = Required.DisallowNull;
        schemaGenerator.GenerationProviders.Add(new StringEnumGenerationProvider());
        JSchema schema = schemaGenerator.Generate(type);
        string result = GenerateRegexFromJsonSchema(schema, chainOfThought, stopToken);
        return result;
    }

    public static string FromObject(object obj, bool chainOfThought, string? stopToken = null)
    {
        JSchemaGenerator schemaGenerator = new JSchemaGenerator();
        schemaGenerator.DefaultRequired = Required.DisallowNull;
        schemaGenerator.GenerationProviders.Add(new StringEnumGenerationProvider());
        JSchema schema = schemaGenerator.Generate(obj.GetType());
        string result = GenerateRegexFromJsonSchema(schema, chainOfThought, stopToken);
        return result;
    }
    
    public static bool MatchesSchema(string input, object obj, bool chainOfThought)
    {
        JSchemaGenerator schemaGenerator = new JSchemaGenerator();
        schemaGenerator.DefaultRequired = Required.DisallowNull;
        schemaGenerator.GenerationProviders.Add(new StringEnumGenerationProvider());
        //JToken jsonObject = JToken.FromObject(obj);
        JSchema schema = schemaGenerator.Generate(obj.GetType());
        string regexPattern = GenerateRegexFromJsonSchema(schema, chainOfThought);
    
        return Regex.IsMatch(input, regexPattern);
    }
    
    private static string GenerateRegexFromJsonSchema(JSchema schema, bool chainOfThought, string? stopToken = null)
    {
        StringBuilder regexBuilder = new StringBuilder();
        if (chainOfThought)
        {
            //regexBuilder.Append(@"^Reasoning:\s*(.*)\nOutput:\s*");
            regexBuilder.Append(@"Reasoning:\s*(.*)\nOutput:\s*");
        }

        AppendRegexForSchemaType(schema, regexBuilder);

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

    private static bool HasFlag(JSchemaType? container, JSchemaType flag)
    {
        return (container & flag) == flag;
    }
    
    private static void AppendRegexForSchemaType(JSchema schema, StringBuilder regexBuilder, bool isRoot = false)
    {
        StringBuilder typeRegexBuilder = new StringBuilder();

        // Construct regex patterns for each applicable type
        if (HasFlag(schema.Type, JSchemaType.Object))
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
                objectRegexBuilder.Append(Regex.Escape(property.Key)); // Escape property name
                objectRegexBuilder.Append("\"\\s*:\\s*");
                AppendRegexForSchemaType(property.Value, objectRegexBuilder); // Recursive call for nested properties
                first = false;
            }
            objectRegexBuilder.Append("\\s*\\}");  // End object with optional whitespace
            AppendOr(typeRegexBuilder, objectRegexBuilder.ToString());
        }

        if (HasFlag(schema.Type, JSchemaType.Array))
        {
            StringBuilder arrayRegexBuilder = new StringBuilder("\\[\\s*"); // Start array with optional whitespace
            if (schema.Items != null && schema.Items.Count > 0)
            {
                AppendRegexForSchemaType(schema.Items[0], arrayRegexBuilder); // Regex for first item type
                arrayRegexBuilder.Append("(?:\\s*,\\s*");
                AppendRegexForSchemaType(schema.Items[0], arrayRegexBuilder); // Regex for subsequent item types
                arrayRegexBuilder.Append(")*?");
            }
            arrayRegexBuilder.Append("\\s*\\]"); // End array with optional whitespace
            AppendOr(typeRegexBuilder, arrayRegexBuilder.ToString());
        }

        // Include regex patterns for other types
        AddSimpleTypeRegex(typeRegexBuilder, schema, JSchemaType.String, "\"[^\"]*\"");
        AddSimpleTypeRegex(typeRegexBuilder, schema, JSchemaType.Integer, "\\d+");
        AddSimpleTypeRegex(typeRegexBuilder, schema, JSchemaType.Number, "-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?");
        AddSimpleTypeRegex(typeRegexBuilder, schema, JSchemaType.Boolean, "(true|false)");
        AddSimpleTypeRegex(typeRegexBuilder, schema, JSchemaType.Null, "null");

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

    private static void AddSimpleTypeRegex(StringBuilder builder, JSchema schema, JSchemaType type, string regexPattern)
    {
        if (HasFlag(schema.Type, type))
        {
            AppendOr(builder, regexPattern);
        }
    }
}

