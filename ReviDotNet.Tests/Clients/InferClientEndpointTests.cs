// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Tests for the provider-level <c>api-version-path</c> override. OpenAI-compatible endpoints
/// normally live under a <c>v1/</c> segment, but some hosts carry the whole version in the base URL
/// with no <c>v1</c> at all (Z.ai: <c>https://api.z.ai/api/paas/v4/chat/completions</c>). These
/// tests lock in the resolved request URL for the default, custom, and "none" settings.
/// </summary>
public class InferClientEndpointTests
{
    private const string OpenAiChatJson =
        "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    [Fact]
    public async Task DefaultVersionPath_UsesV1ChatCompletions()
    {
        var handler = new CapturingHandler();
        using InferClient client = CreateClient("https://api.example.com/", apiVersionPath: null, handler);

        await client.GenerateAsync(new List<Message> { new("user", "hi") }, model: "m");

        handler.Uris.Should().ContainSingle().Which.Should().Be("https://api.example.com/v1/chat/completions");
    }

    [Fact]
    public async Task NoneVersionPath_OmitsTheVersionSegment()
    {
        var handler = new CapturingHandler();
        using InferClient client = CreateClient("https://api.z.ai/api/paas/v4/", apiVersionPath: "none", handler);

        await client.GenerateAsync(new List<Message> { new("user", "hi") }, model: "glm-5.2");

        handler.Uris.Should().ContainSingle().Which.Should().Be("https://api.z.ai/api/paas/v4/chat/completions",
            "Z.ai's OpenAI-compatible endpoint has no v1 segment — the base URL already carries the version");
    }

    [Fact]
    public async Task CustomVersionPath_IsUsedVerbatim()
    {
        var handler = new CapturingHandler();
        using InferClient client = CreateClient("https://api.example.com/", apiVersionPath: "v2", handler);

        await client.GenerateAsync(new List<Message> { new("user", "hi") }, model: "m");

        handler.Uris.Should().ContainSingle().Which.Should().Be("https://api.example.com/v2/chat/completions");
    }

    // ==========
    //  Helpers
    // ==========

    /// <summary>Builds an OpenAI-protocol client whose HTTP layer is replaced by the capturing handler.</summary>
    /// <param name="apiUrl">The provider base URL.</param>
    /// <param name="apiVersionPath">The version-segment override under test (null = default).</param>
    /// <param name="handler">The handler that records outgoing request URIs.</param>
    /// <returns>The configured client.</returns>
    private static InferClient CreateClient(string apiUrl, string? apiVersionPath, CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(apiUrl) };
        return new InferClient(
            apiUrl: apiUrl,
            apiKey: "test",
            protocol: Protocol.OpenAI,
            defaultModel: "m",
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            apiVersionPath: apiVersionPath,
            httpClientOverride: http);
    }

    /// <summary>Records every outgoing request URI and returns a canned OpenAI chat response.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        /// <summary>The absolute URIs of every request the client sent.</summary>
        public List<string> Uris { get; } = [];

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OpenAiChatJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
