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
/// Gemini's <c>responseSchema</c> is an OpenAPI-subset proto, not full JSON Schema: it rejects
/// <c>$schema</c> and <c>additionalProperties</c>, and a <c>type</c> must be a single value, not an
/// array. A raw <see cref="AgentStepSchema"/> therefore gets a 400 ("Unknown name ... Cannot find
/// field" / "Proto field is not repeating") — verified against the live API. These tests lock in that
/// <c>TransformToGeminiPayload</c> rewrites the schema into a form Gemini accepts.
/// </summary>
public class GeminiSchemaSanitizerTests
{
    private static Dictionary<string, object> ResponseSchemaFor(string schema)
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
        var generationConfig = (Dictionary<string, object>)gemini["generationConfig"];
        generationConfig.Should().ContainKey("responseSchema");
        return (Dictionary<string, object>)generationConfig["responseSchema"];
    }

    [Fact]
    public void AgentStepSchema_IsRewrittenToGeminiCompatibleForm()
    {
        Dictionary<string, object> responseSchema = ResponseSchemaFor(AgentStepSchema.Schema);

        // Serialize the whole rewritten schema and assert the Gemini-incompatible constructs are gone.
        string json = JsonConvert.SerializeObject(responseSchema);

        json.Should().NotContain("$schema", "Gemini rejects the $schema keyword");
        json.Should().NotContain("additionalProperties", "Gemini rejects additionalProperties anywhere in the tree");
        json.Should().NotContain("\"null\"", "array-valued nullable types must be collapsed to a single type + nullable flag");

        // The nullable union fields (signal, thinking) must become a single string type + nullable=true.
        var properties = (Dictionary<string, object>)responseSchema["properties"];

        var signal = (Dictionary<string, object>)properties["signal"];
        signal["type"].Should().Be("string");
        signal.Should().ContainKey("nullable").WhoseValue.Should().Be(true);

        var thinking = (Dictionary<string, object>)properties["thinking"];
        thinking["type"].Should().Be("string");
        thinking.Should().ContainKey("nullable").WhoseValue.Should().Be(true);

        // Non-nullable fields keep their plain type and gain no nullable flag.
        var content = (Dictionary<string, object>)properties["content"];
        content["type"].Should().Be("string");
        content.Should().NotContainKey("nullable");
    }

    [Fact]
    public void NestedAdditionalProperties_AreStripped()
    {
        // The tool_calls items object carries additionalProperties:false in the source schema.
        Dictionary<string, object> responseSchema = ResponseSchemaFor(AgentStepSchema.Schema);

        var properties = (Dictionary<string, object>)responseSchema["properties"];
        var toolCalls = (Dictionary<string, object>)properties["tool_calls"];
        var items = (Dictionary<string, object>)toolCalls["items"];

        items.Should().NotContainKey("additionalProperties");
        items["type"].Should().Be("object");
        // Recursion must still reach the leaf properties.
        var itemProps = (Dictionary<string, object>)items["properties"];
        itemProps.Should().ContainKeys("name", "input");
    }
}
