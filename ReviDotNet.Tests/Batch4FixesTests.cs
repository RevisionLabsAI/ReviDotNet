// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 4 audit fixes (D32 per-model embed overrides, D37 ListInputs guard).
/// </summary>
public class Batch4FixesTests
{
    // ── D37: a Listed/Both input type without single-item/multi-item now throws a clear error (not an NRE) ──

    [Fact]
    public void ListInputs_ListedInputWithoutTemplate_ThrowsClearError()
    {
        var model = new ModelProfile { Name = "m" }; // InputItem / InputItemMulti are null
        var inputs = new List<Input> { new Input("city", "LA") };

        Action act = () => Infer.ListInputs(model, inputs);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*single-item*");
    }

    [Fact]
    public void ListInputs_WithTemplate_FormatsInput()
    {
        var model = new ModelProfile { Name = "m", InputItem = "{label}: {text}" };
        var inputs = new List<Input> { new Input("city", "LA") };

        string? result = Infer.ListInputs(model, inputs);

        result.Should().Be("city: LA");
    }

    // ── D32: per-model embedding timeout/retry overrides are accepted and the request still succeeds ──

    [Fact]
    public async Task GenerateEmbeddingAsync_WithPerModelOverrides_Succeeds()
    {
        var (server, baseAddress) = FakeInferenceServer.Create();
        using var _ = server;
        var httpClient = server.CreateClient();
        httpClient.BaseAddress = baseAddress;

        using var client = new EmbedClient(
            apiUrl: baseAddress.ToString(),
            apiKey: "test-key",
            protocol: Protocol.OpenAI,
            defaultModel: "text-embedding-ada-002",
            timeoutSeconds: 30,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            httpClientOverride: httpClient);

        // Exercises the per-request timeout-CTS branch and the retry-limit override plumbing (D32).
        var response = await client.GenerateEmbeddingAsync(
            "hello",
            timeoutSecondsOverride: 15,
            retryAttemptLimitOverride: 3);

        response.Should().NotBeNull();
        response.Data.Should().ContainSingle();
        response.Data[0].Embedding.Should().NotBeEmpty();
    }
}
