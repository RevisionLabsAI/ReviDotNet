// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json.Nodes;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Pins that a YAML few-shot example reaches the model with the scalar types the schema
/// describes. <c>Revi.Util.ConvertYamlToJson</c> used to deserialize into <c>object</c>, which made
/// every scalar a string: a prompt example written <c>is-parked: false</c> went on the wire as
/// <c>"is-parked": "false"</c> beside a schema that said <c>boolean</c>. Four BetterNamer prompts
/// contradicted their own schema that way.
/// </summary>
public sealed class YamlExampleTypingTests
{
    /// <summary>Plain booleans, integers, floats and nulls keep their YAML type; quoted scalars stay strings.</summary>
    [Fact]
    public void Plain_scalars_are_typed_and_quoted_scalars_stay_strings()
    {
        const string yaml = """
            have-question: true
            is-parked: false
            count: 42
            score: 7.5
            question: null
            tilde: ~
            quoted-bool: "true"
            quoted-number: '42'
            version: 1.0.3
            text: Marketplace for services
            """;

        JsonObject json = JsonNode.Parse(Revi.Util.ConvertYamlToJson(yaml))!.AsObject();

        json["have-question"]!.GetValue<bool>().Should().BeTrue();
        json["is-parked"]!.GetValue<bool>().Should().BeFalse();
        json["count"]!.GetValue<long>().Should().Be(42);
        json["score"]!.GetValue<double>().Should().Be(7.5);
        json["question"].Should().BeNull();
        json["tilde"].Should().BeNull();
        json["quoted-bool"]!.GetValue<string>().Should().Be("true");
        json["quoted-number"]!.GetValue<string>().Should().Be("42");
        json["version"]!.GetValue<string>().Should().Be("1.0.3", "three dotted parts is not a number");
        json["text"]!.GetValue<string>().Should().Be("Marketplace for services");
    }

    /// <summary>Nested mappings and sequences convert structurally, with typed leaves.</summary>
    [Fact]
    public void Nested_structures_convert_with_typed_leaves()
    {
        const string yaml = """
            options:
              - Marketplace for services
              - 3
              - true
            detail:
              nested: yes-this-is-a-string
              flag: false
            """;

        JsonObject json = JsonNode.Parse(Revi.Util.ConvertYamlToJson(yaml))!.AsObject();

        JsonArray options = json["options"]!.AsArray();
        options.Count.Should().Be(3);
        options[0]!.GetValue<string>().Should().Be("Marketplace for services");
        options[1]!.GetValue<long>().Should().Be(3);
        options[2]!.GetValue<bool>().Should().BeTrue();
        json["detail"]!["nested"]!.GetValue<string>().Should().Be("yes-this-is-a-string");
        json["detail"]!["flag"]!.GetValue<bool>().Should().BeFalse();
    }

    /// <summary>An apostrophe in a value is written literally, not as a ' escape the model would copy.</summary>
    [Fact]
    public void Apostrophes_are_not_unicode_escaped()
    {
        string json = Revi.Util.ConvertYamlToJson("summary: \"The bakery's site is gone.\"");

        json.Should().Contain("bakery's").And.NotContain("\\u0027");
    }

    /// <summary>The request-json example path produces typed JSON end to end.</summary>
    [Fact]
    public void JsonifyExample_types_a_yaml_example()
    {
        string json = Revi.Util.JsonifyExample("is-parked: false\nsummary: A parked page.", requestJson: true);

        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        obj["is-parked"]!.GetValue<bool>().Should().BeFalse();
        obj["summary"]!.GetValue<string>().Should().Be("A parked page.");
    }

    /// <summary>An empty document converts to an empty string rather than throwing.</summary>
    [Fact]
    public void An_empty_document_is_an_empty_string()
    {
        Revi.Util.ConvertYamlToJson("").Should().BeEmpty();
    }
}
