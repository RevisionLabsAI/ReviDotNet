// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Verifies portable provider-routing parsing, validation, and OpenRouter request translation.
/// </summary>
public sealed class OpenRouterRoutingTests
{
    /// <summary>Verifies every common routing field maps to OpenRouter's native provider object.</summary>
    [Fact]
    public void CommonPolicy_MapsToOpenRouterProviderObject()
    {
        ProviderRoutingPolicy policy = new()
        {
            UpstreamOrder = ["deepinfra", "together"],
            AllowedUpstreams = ["deepinfra", "together", "novita"],
            BlockedUpstreams = ["example"],
            AllowFallbacks = false,
            SelectionStrategy = RoutingSelectionStrategy.LowestCost,
            RequireRequestParameterSupport = true,
            AllowedQuantizations = ["bf16", "fp16"],
            MaximumCostPerMillionInputTokens = 0.5m,
            MaximumCostPerMillionOutputTokens = 2m,
            PreferredMinimumThroughputTokensPerSecond = 40,
            PreferredMaximumLatencySeconds = 2,
            DataRetention = DataRetentionPolicy.None,
            DataCollection = DataCollectionPolicy.Deny,
            DataResidency = DataResidencyPolicy.Any
        };
        PayloadTransformer transformer = CreateTransformer(Protocol.OpenRouter, policy);
        Dictionary<string, object> payload = [];

        ApplyOptionalParameters(transformer, payload);

        payload.Should().ContainKey("provider");
        JObject provider = JObject.FromObject(payload["provider"]);
        provider["order"]!.Values<string>().Should().Equal("deepinfra", "together");
        provider["only"]!.Values<string>().Should().Equal("deepinfra", "together", "novita");
        provider["ignore"]!.Values<string>().Should().Equal("example");
        provider["quantizations"]!.Values<string>().Should().Equal("bf16", "fp16");
        provider["allow_fallbacks"]!.Value<bool>().Should().BeFalse();
        provider["require_parameters"]!.Value<bool>().Should().BeTrue();
        provider["sort"]!.Value<string>().Should().Be("price");
        provider["preferred_min_throughput"]!.Value<double>().Should().Be(40);
        provider["preferred_max_latency"]!.Value<double>().Should().Be(2);
        provider["zdr"]!.Value<bool>().Should().BeTrue();
        provider["data_collection"]!.Value<string>().Should().Be("deny");
        provider["max_price"]!["prompt"]!.Value<decimal>().Should().Be(0.5m);
        provider["max_price"]!["completion"]!.Value<decimal>().Should().Be(2m);
        provider.Should().NotContainKey("region", "OpenRouter residency is selected by the API base URL");
    }

    /// <summary>Verifies OpenRouter retains shared OpenAI reasoning and JSON-guidance behavior.</summary>
    [Fact]
    public void OpenRouterProtocol_RetainsOpenAiReasoningAndGuidance()
    {
        PayloadTransformer transformer = CreateTransformer(
            Protocol.OpenRouter,
            new ProviderRoutingPolicy { DataRetention = DataRetentionPolicy.None },
            supportsGuidance: true);
        Dictionary<string, object> payload =
            new() { ["messages"] = new List<Message> { new("user", "hello") } };

        transformer.AddOptionalParameters(
            payload,
            temperature: null,
            topK: null,
            topP: null,
            minP: null,
            bestOf: null,
            maxTokenType: null,
            maxTokens: null,
            frequencyPenalty: null,
            presencePenalty: null,
            repetitionPenalty: null,
            stopSequences: null,
            guidanceType: GuidanceType.Json,
            guidanceString: """{"type":"object"}""",
            useSearchGrounding: null,
            thinking: "high");

        payload["reasoning_effort"].Should().Be("high");
        payload.Should().ContainKey("response_format");
        JObject.FromObject(payload["provider"])["zdr"]!.Value<bool>().Should().BeTrue();
    }

    /// <summary>Verifies ordinary OpenAI requests are unchanged when no routing policy is configured.</summary>
    [Fact]
    public void OpenAiWithoutPolicy_EmitsNoProviderObject()
    {
        PayloadTransformer transformer = CreateTransformer(Protocol.OpenAI, routingPolicy: null);
        Dictionary<string, object> payload = [];

        ApplyOptionalParameters(transformer, payload);

        payload.Should().NotContainKey("provider");
    }

    /// <summary>Verifies a protocol cannot silently ignore an explicitly configured routing policy.</summary>
    [Fact]
    public void ProtocolWithoutRoutingAdapter_RejectsConfiguredPolicy()
    {
        System.Action act = () => CreateTransformer(
            Protocol.OpenAI,
            new ProviderRoutingPolicy { DataRetention = DataRetentionPolicy.None });

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*has no routing adapter*");
    }

    /// <summary>Verifies contradictory upstream filters are rejected before serialization.</summary>
    [Fact]
    public void ContradictoryUpstreamFilters_AreRejected()
    {
        ProviderRoutingPolicy policy = new()
        {
            AllowedUpstreams = ["deepinfra"],
            BlockedUpstreams = ["DeepInfra"]
        };
        System.Action act = () => CreateTransformer(Protocol.OpenRouter, policy);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*both allowed and blocked*");
    }

    /// <summary>Verifies EU residency cannot be implied while using OpenRouter's global endpoint.</summary>
    [Fact]
    public void EuResidency_WithGlobalOpenRouterUrl_IsRejected()
    {
        ProviderRoutingPolicy policy = new() { DataResidency = DataResidencyPolicy.EU };
        System.Action act = () => CreateTransformer(Protocol.OpenRouter, policy);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*eu.openrouter.ai*");
    }

    /// <summary>Verifies the complete RConfig vocabulary deserializes into the provider profile.</summary>
    [Fact]
    public void RoutingRConfig_ParsesPortableSettings()
    {
        const string content = """
            [[general]]
            name = openrouter-test
            enabled = true
            protocol = OpenRouter
            api-url = https://openrouter.ai/api/
            api-key = test

            [[routing]]
            upstream-order = deepinfra, together
            allowed-upstreams = deepinfra together novita
            blocked-upstreams = example
            allow-fallbacks = false
            selection-strategy = highest-throughput
            require-request-parameter-support = true
            allowed-quantizations = bf16 fp16
            maximum-cost-per-million-input-tokens = 0.5
            maximum-cost-per-million-output-tokens = 2
            preferred-minimum-throughput-tokens-per-second = 40
            preferred-maximum-latency-seconds = 2.5
            data-retention = none
            data-collection = deny
            data-residency = any
            """;

        Dictionary<string, string> data = RConfigParser.ReadEmbedded(content);
        ProviderProfile provider = RConfigParser.ToObject<ProviderProfile>(data)!;

        try
        {
            provider.Protocol.Should().Be(Protocol.OpenRouter);
            provider.UpstreamOrder.Should().Equal("deepinfra", "together");
            provider.AllowedUpstreams.Should().Equal("deepinfra", "together", "novita");
            provider.BlockedUpstreams.Should().Equal("example");
            provider.AllowFallbacks.Should().BeFalse();
            provider.SelectionStrategy.Should().Be(RoutingSelectionStrategy.HighestThroughput);
            provider.RequireRequestParameterSupport.Should().BeTrue();
            provider.AllowedQuantizations.Should().Equal("bf16", "fp16");
            provider.MaximumRoutingCostPerMillionInputTokens.Should().Be(0.5m);
            provider.MaximumRoutingCostPerMillionOutputTokens.Should().Be(2m);
            provider.PreferredMinimumThroughputTokensPerSecond.Should().Be(40);
            provider.PreferredMaximumLatencySeconds.Should().Be(2.5);
            provider.DataRetention.Should().Be(DataRetentionPolicy.None);
            provider.DataCollection.Should().Be(DataCollectionPolicy.Deny);
            provider.DataResidency.Should().Be(DataResidencyPolicy.Any);
            provider.InferenceClient.Should().NotBeNull();
        }
        finally
        {
            provider.InferenceClient?.Dispose();
            provider.EmbeddingClient?.Dispose();
        }
    }

    /// <summary>Builds a payload transformer for a protocol and routing policy.</summary>
    /// <param name="protocol">The provider protocol.</param>
    /// <param name="routingPolicy">The routing policy, if configured.</param>
    /// <param name="supportsGuidance">Whether JSON guidance is enabled.</param>
    /// <returns>The configured payload transformer.</returns>
    private static PayloadTransformer CreateTransformer(
        Protocol protocol,
        ProviderRoutingPolicy? routingPolicy,
        bool supportsGuidance = false)
    {
        return new PayloadTransformer(new InferClientConfig
        {
            ApiUrl = "https://openrouter.ai/api/",
            ApiKey = "test",
            Protocol = protocol,
            DefaultModel = "test-model",
            SupportsGuidance = supportsGuidance,
            RoutingPolicy = routingPolicy
        });
    }

    /// <summary>Invokes optional-parameter processing without unrelated generation settings.</summary>
    /// <param name="transformer">The payload transformer under test.</param>
    /// <param name="payload">The payload to augment.</param>
    private static void ApplyOptionalParameters(PayloadTransformer transformer, Dictionary<string, object> payload)
    {
        transformer.AddOptionalParameters(
            payload,
            temperature: null,
            topK: null,
            topP: null,
            minP: null,
            bestOf: null,
            maxTokenType: null,
            maxTokens: null,
            frequencyPenalty: null,
            presencePenalty: null,
            repetitionPenalty: null,
            stopSequences: null,
            guidanceType: null,
            guidanceString: null,
            useSearchGrounding: null);
    }
}
