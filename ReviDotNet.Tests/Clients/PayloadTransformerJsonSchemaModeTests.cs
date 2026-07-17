// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Tests for the per-provider <c>json-schema-mode</c> switch. OpenAI-protocol providers default to
/// strict <c>response_format: json_schema</c>, but hosts that reject that form (e.g. Z.ai/GLM, which
/// only accepts <c>json_object</c>) must be able to downgrade — sending <c>json_object</c> on the
/// wire and delivering the schema as an appended system message instead.
/// </summary>
public class PayloadTransformerJsonSchemaModeTests
{
    private const string Schema = """{"type":"object","properties":{"name":{"type":"string"}}}""";

    /// <summary>Builds an OpenAI-protocol transformer with the given JSON schema mode.</summary>
    /// <param name="mode">The wire mode under test.</param>
    /// <returns>A transformer whose provider supports JSON guidance.</returns>
    private static PayloadTransformer OpenAiTransformer(JsonSchemaMode mode) => new(new InferClientConfig
    {
        ApiUrl = "https://api.example.com/v1/",
        ApiKey = "test",
        Protocol = Protocol.OpenAI,
        DefaultModel = "test-model",
        SupportsGuidance = true,
        JsonSchemaMode = mode
    });

    /// <summary>Invokes AddOptionalParameters with only guidance-related arguments set.</summary>
    /// <param name="transformer">The transformer under test.</param>
    /// <param name="parameters">The payload dictionary to populate.</param>
    private static void AddGuidance(PayloadTransformer transformer, Dictionary<string, object> parameters)
    {
        transformer.AddOptionalParameters(
            parameters,
            temperature: null, topK: null, topP: null, minP: null, bestOf: null,
            maxTokenType: null, maxTokens: null,
            frequencyPenalty: null, presencePenalty: null, repetitionPenalty: null,
            stopSequences: null,
            guidanceType: GuidanceType.Json,
            guidanceString: Schema,
            useSearchGrounding: null);
    }

    [Fact]
    public void DefaultMode_EmitsStrictJsonSchemaResponseFormat()
    {
        Dictionary<string, object> parameters = new()
        {
            ["messages"] = new List<Message> { new("user", "hi") }
        };

        AddGuidance(OpenAiTransformer(JsonSchemaMode.JsonSchema), parameters);

        parameters.Should().ContainKey("response_format");
        JObject responseFormat = JObject.FromObject(parameters["response_format"]);
        responseFormat["type"]!.Value<string>().Should().Be("json_schema");
        responseFormat["json_schema"]!["strict"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void JsonObjectMode_EmitsJsonObjectResponseFormat_WithoutSchemaOnTheWire()
    {
        Dictionary<string, object> parameters = new()
        {
            ["messages"] = new List<Message> { new("user", "hi") }
        };

        AddGuidance(OpenAiTransformer(JsonSchemaMode.JsonObject), parameters);

        parameters.Should().ContainKey("response_format");
        JObject responseFormat = JObject.FromObject(parameters["response_format"]);
        responseFormat["type"]!.Value<string>().Should().Be("json_object",
            "hosts like Z.ai reject the json_schema form and only accept json_object");
        responseFormat.ContainsKey("json_schema").Should().BeFalse();
    }

    [Fact]
    public void JsonObjectMode_AppendsSchemaAsSystemMessage_WithoutMutatingCallerList()
    {
        List<Message> original = [new("system", "sys"), new("user", "hi")];
        Dictionary<string, object> parameters = new() { ["messages"] = original };

        AddGuidance(OpenAiTransformer(JsonSchemaMode.JsonObject), parameters);

        List<Message> augmented = (List<Message>)parameters["messages"];
        augmented.Should().HaveCount(3);
        augmented[2].Role.Should().Be("system");
        augmented[2].Content.Should().Contain(Schema, "the model must still learn the expected shape from the prompt");
        original.Should().HaveCount(2, "the caller's conversation history must not accumulate schema instructions");
    }

    [Fact]
    public void JsonObjectMode_WithoutGuidance_EmitsNoResponseFormat()
    {
        Dictionary<string, object> parameters = new()
        {
            ["messages"] = new List<Message> { new("user", "hi") }
        };

        OpenAiTransformer(JsonSchemaMode.JsonObject).AddOptionalParameters(
            parameters,
            temperature: null, topK: null, topP: null, minP: null, bestOf: null,
            maxTokenType: null, maxTokens: null,
            frequencyPenalty: null, presencePenalty: null, repetitionPenalty: null,
            stopSequences: null, guidanceType: null, guidanceString: null, useSearchGrounding: null);

        parameters.Should().NotContainKey("response_format");
        ((List<Message>)parameters["messages"]).Should().HaveCount(1);
    }
}
