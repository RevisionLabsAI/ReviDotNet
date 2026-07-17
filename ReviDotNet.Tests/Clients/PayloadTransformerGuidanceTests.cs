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
/// Tests for the modernized per-protocol guidance emission: Claude structured outputs
/// (<c>output_config.format</c>), the current vLLM wire forms (<c>response_format json_schema</c>
/// for JSON, <c>structured_outputs</c> for regex — the legacy <c>guided_json</c>/
/// <c>guided_decoding_backend</c> fields were removed in vLLM v0.12), and the Responses-API
/// rewrite of <c>response_format</c> into the flattened <c>text.format</c> shape.
/// </summary>
public class PayloadTransformerGuidanceTests
{
    private const string Schema = """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""";

    /// <summary>Builds a transformer for the given protocol with guidance enabled.</summary>
    /// <param name="protocol">The protocol under test.</param>
    /// <returns>The configured transformer.</returns>
    private static PayloadTransformer Transformer(Protocol protocol) => new(new InferClientConfig
    {
        ApiUrl = "https://api.example.com/",
        ApiKey = "test",
        Protocol = protocol,
        DefaultModel = "m",
        SupportsGuidance = true
    });

    /// <summary>Invokes AddOptionalParameters with only guidance-related arguments set.</summary>
    /// <param name="transformer">The transformer under test.</param>
    /// <param name="parameters">The payload dictionary to populate.</param>
    private static void AddJsonGuidance(PayloadTransformer transformer, Dictionary<string, object> parameters)
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

    // ==========
    //  Claude
    // ==========

    [Fact]
    public void Claude_JsonGuidance_EmitsOutputConfigFormat()
    {
        Dictionary<string, object> payload = new() { ["messages"] = new List<Message> { new("user", "hi") } };
        PayloadTransformer transformer = Transformer(Protocol.Claude);

        AddJsonGuidance(transformer, payload);
        Dictionary<string, object> claude = transformer.TransformToClaudePayload(payload);

        claude.Should().ContainKey("output_config");
        var outputConfig = (Dictionary<string, object>)claude["output_config"];
        var format = (Dictionary<string, object>)outputConfig["format"];
        format["type"].Should().Be("json_schema");
        format.Should().ContainKey("schema");
        claude.Should().NotContainKey("guided_json", "the stash key must not leak onto the wire");
    }

    [Fact]
    public void Claude_JsonGuidance_MergesWithThinkingEffortOutputConfig()
    {
        Dictionary<string, object> payload = new()
        {
            ["messages"] = new List<Message> { new("user", "hi") },
            ["thinking_mode"] = "high"
        };
        PayloadTransformer transformer = Transformer(Protocol.Claude);

        AddJsonGuidance(transformer, payload);
        Dictionary<string, object> claude = transformer.TransformToClaudePayload(payload);

        var outputConfig = (Dictionary<string, object>)claude["output_config"];
        outputConfig.Should().ContainKey("effort", "the adaptive-thinking effort must survive the merge");
        outputConfig.Should().ContainKey("format", "structured outputs must coexist with effort");
    }

    [Fact]
    public void Claude_GuidanceUnsupported_EmitsNothing()
    {
        var transformer = new PayloadTransformer(new InferClientConfig
        {
            ApiUrl = "https://api.example.com/",
            ApiKey = "test",
            Protocol = Protocol.Claude,
            DefaultModel = "m",
            SupportsGuidance = false
        });
        Dictionary<string, object> payload = new() { ["messages"] = new List<Message> { new("user", "hi") } };

        AddJsonGuidance(transformer, payload);
        Dictionary<string, object> claude = transformer.TransformToClaudePayload(payload);

        claude.Should().NotContainKey("output_config");
    }

    // ==========
    //  vLLM
    // ==========

    [Fact]
    public void Vllm_JsonGuidance_UsesOpenAiResponseFormat_NotLegacyGuidedJson()
    {
        Dictionary<string, object> parameters = new();

        AddJsonGuidance(Transformer(Protocol.vLLM), parameters);

        parameters.Should().NotContainKey("guided_json", "removed in vLLM v0.12");
        parameters.Should().NotContainKey("guided_decoding_backend", "removed in vLLM v0.12; backend is server-side config now");
        parameters.Should().ContainKey("response_format");
        JObject responseFormat = JObject.FromObject(parameters["response_format"]);
        responseFormat["type"]!.Value<string>().Should().Be("json_schema");
    }

    [Fact]
    public void Vllm_RegexGuidance_UsesStructuredOutputs()
    {
        Dictionary<string, object> parameters = new();

        Transformer(Protocol.vLLM).AddOptionalParameters(
            parameters,
            temperature: null, topK: null, topP: null, minP: null, bestOf: null,
            maxTokenType: null, maxTokens: null,
            frequencyPenalty: null, presencePenalty: null, repetitionPenalty: null,
            stopSequences: null,
            guidanceType: GuidanceType.Regex,
            guidanceString: "[a-z]+",
            useSearchGrounding: null);

        parameters.Should().NotContainKey("guided_regex", "removed in vLLM v0.12");
        parameters.Should().ContainKey("structured_outputs");
        var structured = (Dictionary<string, object>)parameters["structured_outputs"];
        structured["regex"].Should().Be("[a-z]+");
    }

    // =====================
    //  Responses API shape
    // =====================

    [Fact]
    public void ResponsesApi_JsonSchemaResponseFormat_IsFlattenedUnderTextFormat()
    {
        Dictionary<string, object> parameters = new();
        AddJsonGuidance(Transformer(Protocol.OpenAI), parameters);

        PayloadTransformer.ConvertResponseFormatForResponsesApi(parameters);

        parameters.Should().NotContainKey("response_format", "the Responses API rejects it");
        parameters.Should().ContainKey("text");
        var text = (Dictionary<string, object>)parameters["text"];
        var format = (Dictionary<string, object>)text["format"];
        format["type"].Should().Be("json_schema");
        format["name"].Should().Be("response_schema");
        format["strict"].Should().Be(true);
        format.Should().ContainKey("schema", "the schema is flattened directly under format — no nested json_schema object");
        format.Should().NotContainKey("json_schema");
    }

    [Fact]
    public void ResponsesApi_JsonObjectResponseFormat_IsConverted()
    {
        Dictionary<string, object> parameters = new()
        {
            ["response_format"] = new { type = "json_object" }
        };

        PayloadTransformer.ConvertResponseFormatForResponsesApi(parameters);

        var text = (Dictionary<string, object>)parameters["text"];
        var format = (Dictionary<string, object>)text["format"];
        format["type"].Should().Be("json_object");
    }

    [Fact]
    public void ResponsesApi_NoResponseFormat_IsANoOp()
    {
        Dictionary<string, object> parameters = new() { ["model"] = "m" };

        PayloadTransformer.ConvertResponseFormatForResponsesApi(parameters);

        parameters.Should().NotContainKey("text");
        parameters["model"].Should().Be("m");
    }
}
