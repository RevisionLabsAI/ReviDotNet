// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Gemini structured output now goes through <c>generationConfig.responseJsonSchema</c> — the
/// standard-JSON-Schema field on Gemini 2.5+/3.x — instead of the legacy OpenAPI-subset
/// <c>responseSchema</c> that required heavy sanitization (stripped keywords, collapsed type
/// unions). These tests lock in that the schema passes through intact (only <c>$schema</c> is
/// dropped) alongside <c>responseMimeType: application/json</c>.
/// </summary>
public class GeminiResponseJsonSchemaTests
{
    private static Dictionary<string, object> GenerationConfigFor(string schema)
    {
        var transformer = new PayloadTransformer(new InferClientConfig
        {
            ApiUrl = "https://generativelanguage.googleapis.com/v1beta/",
            ApiKey = "test",
            Protocol = Protocol.Gemini,
            DefaultModel = "gemini-2.5-flash",
            SupportsGuidance = true
        });

        var payload = new Dictionary<string, object> { ["guided_json"] = schema };
        Dictionary<string, object> gemini = transformer.TransformToGeminiPayload(payload);

        gemini.Should().ContainKey("generationConfig");
        return (Dictionary<string, object>)gemini["generationConfig"];
    }

    [Fact]
    public void Schema_IsEmittedAsResponseJsonSchema_WithJsonMimeType()
    {
        Dictionary<string, object> generationConfig = GenerationConfigFor(AgentStepSchema.Schema);

        generationConfig.Should().ContainKey("responseJsonSchema");
        generationConfig.Should().NotContainKey("responseSchema", "the legacy OpenAPI-subset field is no longer used");
        generationConfig.Should().ContainKey("responseMimeType").WhoseValue.Should().Be("application/json");
    }

    [Fact]
    public void Schema_PassesThroughIntact_ExceptMetaSchemaUri()
    {
        Dictionary<string, object> generationConfig = GenerationConfigFor(AgentStepSchema.Schema);
        var schema = (Dictionary<string, object>)generationConfig["responseJsonSchema"];
        string json = JsonConvert.SerializeObject(schema);

        schema.Should().NotContainKey("$schema", "the meta-schema URI is useless to Gemini");

        // responseJsonSchema is standard JSON Schema — the constructs the old sanitizer had to
        // rewrite (nullable type unions) now pass through unchanged.
        var properties = (Dictionary<string, object>)schema["properties"];
        var signal = (Dictionary<string, object>)properties["signal"];
        JsonConvert.SerializeObject(signal["type"]).Should().Contain("null",
            "nullable type unions are valid standard JSON Schema and must survive");
        json.Should().Contain("required");
    }

    [Fact]
    public void InvalidSchema_EmitsNoResponseJsonSchema()
    {
        var transformer = new PayloadTransformer(new InferClientConfig
        {
            ApiUrl = "https://generativelanguage.googleapis.com/v1beta/",
            ApiKey = "test",
            Protocol = Protocol.Gemini,
            DefaultModel = "gemini-2.5-flash",
            SupportsGuidance = true
        });

        Dictionary<string, object> gemini = transformer.TransformToGeminiPayload(
            new Dictionary<string, object> { ["guided_json"] = "{not json" });

        if (gemini.TryGetValue("generationConfig", out object? cfg))
        {
            ((Dictionary<string, object>)cfg).Should().NotContainKey("responseJsonSchema");
        }
    }
}
