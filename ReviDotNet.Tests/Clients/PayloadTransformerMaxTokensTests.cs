// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Regression tests for max-tokens propagation. A model rcfg that sets <c>max-tokens</c> but no
/// <c>max-token-type</c> (the common case — none of the Claude model files set a type) used to have its
/// value silently DROPPED by <see cref="PayloadTransformer.AddOptionalParameters"/>, because a null type
/// matched neither switch case. Anthropic then applied the transformer's low fallback and truncated 90%
/// of live campaign outputs and 100% of judge verdicts at 1024 tokens.
/// </summary>
public class PayloadTransformerMaxTokensTests
{
    private static PayloadTransformer ClaudeTransformer() => new(new InferClientConfig
    {
        ApiUrl = "https://api.anthropic.com/v1/",
        ApiKey = "test",
        Protocol = Protocol.Claude,
        DefaultModel = "claude-sonnet-4-6",
        SupportsGuidance = false
    });

    [Fact]
    public void NullMaxTokenType_StillEmitsMaxTokens()
    {
        var parameters = new Dictionary<string, object>();

        ClaudeTransformer().AddOptionalParameters(
            parameters,
            temperature: null, topK: null, topP: null, minP: null, bestOf: null,
            maxTokenType: null,          // no max-token-type in the model rcfg — must default to max_tokens
            maxTokens: 4096,
            frequencyPenalty: null, presencePenalty: null, repetitionPenalty: null,
            stopSequences: null, guidanceType: null, guidanceString: null, useSearchGrounding: null);

        parameters.Should().ContainKey("max_tokens")
            .WhoseValue.Should().Be(4096, "a configured max-tokens must reach the payload even when the rcfg sets no max-token-type");
    }

    [Fact]
    public void ExplicitMaxCompletionTokens_EmitsTheAlternateKey()
    {
        var parameters = new Dictionary<string, object>();

        ClaudeTransformer().AddOptionalParameters(
            parameters,
            temperature: null, topK: null, topP: null, minP: null, bestOf: null,
            maxTokenType: MaxTokenType.MaxCompletionTokens,
            maxTokens: 4096,
            frequencyPenalty: null, presencePenalty: null, repetitionPenalty: null,
            stopSequences: null, guidanceType: null, guidanceString: null, useSearchGrounding: null);

        parameters.Should().ContainKey("max_completion_tokens").WhoseValue.Should().Be(4096);
        parameters.Should().NotContainKey("max_tokens", "reasoning-model rcfgs that declare the alternate key must keep it");
    }

    [Fact]
    public void NoMaxTokensConfigured_EmitsNeitherKey()
    {
        var parameters = new Dictionary<string, object>();

        ClaudeTransformer().AddOptionalParameters(
            parameters,
            temperature: null, topK: null, topP: null, minP: null, bestOf: null,
            maxTokenType: null,
            maxTokens: null,
            frequencyPenalty: null, presencePenalty: null, repetitionPenalty: null,
            stopSequences: null, guidanceType: null, guidanceString: null, useSearchGrounding: null);

        parameters.Should().NotContainKey("max_tokens");
        parameters.Should().NotContainKey("max_completion_tokens");
    }

    [Fact]
    public void ClaudePayload_PassesConfiguredMaxTokensThrough()
    {
        var payload = new Dictionary<string, object> { ["max_tokens"] = 4096, ["prompt"] = "hi" };

        Dictionary<string, object> outPayload = ClaudeTransformer().TransformToClaudePayload(payload);

        outPayload["max_tokens"].Should().Be(4096);
    }

    [Fact]
    public void ClaudePayload_FallbackIs4096_NotTheOld1024()
    {
        var payload = new Dictionary<string, object> { ["prompt"] = "hi" };

        Dictionary<string, object> outPayload = ClaudeTransformer().TransformToClaudePayload(payload);

        outPayload["max_tokens"].Should().Be(4096,
            "the required-field fallback must be big enough not to truncate real answers or structured outputs");
    }
}
