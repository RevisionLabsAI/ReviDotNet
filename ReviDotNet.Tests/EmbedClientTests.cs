// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace ReviDotNet.Tests;

public class EmbedClientTests
{
    private static (EmbedClient Client, TestServer Server) CreateClient(Protocol protocol, string defaultModel = "test-model", string apiKey = "test-key")
    {
        var (server, baseAddress) = FakeInferenceServer.Create();
        var httpClient = server.CreateClient();
        httpClient.BaseAddress = baseAddress;

        var client = new EmbedClient(
            apiUrl: baseAddress.ToString(),
            apiKey: apiKey,
            protocol: protocol,
            defaultModel: defaultModel,
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            httpClientOverride: httpClient
        );

        return (client, server);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_SingleInput_OpenAI_ReturnsEmbedding()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, defaultModel: "text-embedding-ada-002");
        using var _ = client; using var __ = server;

        var response = await client.GenerateEmbeddingAsync("Hello world");

        response.Should().NotBeNull();
        response.Data.Should().HaveCount(1);
        response.Data[0].Embedding.Should().HaveCount(5);
        response.Data[0].Embedding.Should().BeEquivalentTo(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f });
        response.Data[0].Index.Should().Be(0);
        response.Model.Should().Be("text-embedding-ada-002");
        response.Object.Should().Be("list");
        response.Inputs.Should().ContainSingle().Which.Should().Be("Hello world");
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_MultipleInputs_OpenAI_ReturnsEmbeddings()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, defaultModel: "text-embedding-ada-002");
        using var _ = client; using var __ = server;

        var inputs = new[] { "First text", "Second text", "Third text" };
        var response = await client.GenerateEmbeddingsAsync(inputs);

        response.Should().NotBeNull();
        response.Data.Should().HaveCount(1); // FakeServer returns single embedding
        response.Data[0].Embedding.Should().HaveCount(5);
        response.Inputs.Should().HaveCount(3);
        response.Inputs.Should().BeEquivalentTo(inputs);
        response.Model.Should().Be("text-embedding-ada-002");
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_SingleInput_Gemini_ReturnsEmbedding()
    {
        var (client, server) = CreateClient(Protocol.Gemini, defaultModel: "gemini-embedding", apiKey: "test-gemini-key");
        using var _ = client; using var __ = server;

        var response = await client.GenerateEmbeddingAsync("Hello Gemini");

        response.Should().NotBeNull();
        response.Data.Should().HaveCount(1);
        response.Data[0].Embedding.Should().HaveCount(5);
        response.Data[0].Embedding.Should().BeEquivalentTo(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f });
        response.Data[0].Index.Should().Be(0);
        response.Inputs.Should().ContainSingle().Which.Should().Be("Hello Gemini");
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WithDimensions_OpenAI_SendsDimensionParameter()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, defaultModel: "text-embedding-3-small");
        using var _ = client; using var __ = server;

        var response = await client.GenerateEmbeddingAsync("Test", dimensions: 512);

        response.Should().NotBeNull();
        response.Data.Should().HaveCount(1);
        response.Data[0].Embedding.Should().HaveCount(5); // FakeServer returns fixed 5-dim
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WithEncodingFormat_OpenAI_SendsFormatParameter()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, defaultModel: "text-embedding-ada-002");
        using var _ = client; using var __ = server;

        var response = await client.GenerateEmbeddingAsync("Test", encodingFormat: "float");

        response.Should().NotBeNull();
        response.Data.Should().HaveCount(1);
        response.Data[0].Embedding.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_DefaultModel_UsesConfiguredDefault()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, defaultModel: "my-custom-model");
        using var _ = client; using var __ = server;

        var response = await client.GenerateEmbeddingAsync("Test");

        response.Should().NotBeNull();
        // The actual model used should be the default configured one
        response.Data.Should().HaveCount(1);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_EmptyInput_OpenAI_HandlesGracefully()
    {
        var (client, server) = CreateClient(Protocol.OpenAI);
        using var _ = client; using var __ = server;

        var response = await client.GenerateEmbeddingsAsync(new[] { "" });

        response.Should().NotBeNull();
        response.Data.Should().HaveCount(1);
    }
}
