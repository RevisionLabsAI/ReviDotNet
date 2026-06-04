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
/// Gemini (Google's Generative Language API) authenticates with the API key in the
/// <c>x-goog-api-key</c> request header. The key must never be placed in the request URL
/// (e.g. <c>?key=SECRET</c>), where it would leak through proxy logs, server access logs,
/// browser history, and any URL-logging path. These tests capture the outgoing request at the
/// <see cref="HttpClient"/> boundary and lock in that the key travels in the header — and only
/// the header — across every Gemini code path: non-streaming inference, streaming inference, and
/// embeddings. A non-Gemini case guards that other protocols still use <c>Authorization: Bearer</c>.
/// </summary>
public class GeminiAuthHeaderTests
{
    private const string ApiKey = "super-secret-gemini-key";

    private const string GeminiChatJson =
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}";

    private const string GeminiSse =
        "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"chunk\"}]},\"finishReason\":\"STOP\"}]}\n\n";

    private const string GeminiEmbedJson =
        "{\"embedding\":{\"values\":[0.1,0.2,0.3]}}";

    private const string OpenAiChatJson =
        "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    [Fact]
    public async Task Gemini_ChatInference_SendsKeyInHeader_NotInUrl()
    {
        var handler = new CapturingHandler(_ => Json(GeminiChatJson));
        using var client = CreateInferClient(Protocol.Gemini, ApiKey, handler);

        await client.GenerateAsync(new List<Message> { new("user", "hi") }, model: "gemini-pro");

        CapturedRequest req = handler.Captured.Should().ContainSingle().Subject;
        req.GoogApiKey.Should().Be(ApiKey);
        req.Authorization.Should().BeNull();
        req.Uri.Should().NotContain("key=");
        req.Uri.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task Gemini_StreamingInference_SendsKeyInHeader_NotInUrl()
    {
        var handler = new CapturingHandler(_ => Sse(GeminiSse));
        using var client = CreateInferClient(Protocol.Gemini, ApiKey, handler);

        var result = client.GenerateStreamAsync(new List<Message> { new("user", "hi") }, model: "gemini-pro");
        await foreach (var _chunk in result.Stream) { /* drain */ }

        CapturedRequest req = handler.Captured.Should().ContainSingle().Subject;
        req.GoogApiKey.Should().Be(ApiKey);
        req.Uri.Should().NotContain("key=");
        req.Uri.Should().NotContain(ApiKey);
        // The unrelated streaming query parameter must survive the removal of the key append.
        req.Uri.Should().Contain("alt=sse");
    }

    [Fact]
    public async Task Gemini_Embeddings_SendKeyInHeader_NotInUrl()
    {
        var handler = new CapturingHandler(_ => Json(GeminiEmbedJson));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://gen.example/") };
        using var client = new EmbedClient(
            apiUrl: "https://gen.example/",
            apiKey: ApiKey,
            protocol: Protocol.Gemini,
            defaultModel: "gemini-embedding",
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            httpClientOverride: http);

        await client.GenerateEmbeddingAsync("hello");

        CapturedRequest req = handler.Captured.Should().ContainSingle().Subject;
        req.GoogApiKey.Should().Be(ApiKey);
        req.Authorization.Should().BeNull();
        req.Uri.Should().NotContain("key=");
        req.Uri.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task NonGemini_ChatInference_UsesBearer_NotGoogHeader()
    {
        var handler = new CapturingHandler(_ => Json(OpenAiChatJson));
        using var client = CreateInferClient(Protocol.OpenAI, ApiKey, handler);

        await client.GenerateAsync(new List<Message> { new("user", "hi") }, model: "gpt-x");

        CapturedRequest req = handler.Captured.Should().ContainSingle().Subject;
        req.GoogApiKey.Should().BeNull();
        req.Authorization.Should().Be($"Bearer {ApiKey}");
        req.Uri.Should().NotContain("key=");
    }

    // ==========
    //  Helpers
    // ==========

    private static InferClient CreateInferClient(Protocol protocol, string apiKey, CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://gen.example/") };
        return new InferClient(
            apiUrl: "https://gen.example/",
            apiKey: apiKey,
            protocol: protocol,
            defaultModel: "gemini-pro",
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            supportsCompletion: true,
            httpClientOverride: http);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    /// <summary>Snapshot of the auth-relevant parts of an outgoing request, taken inside the handler.</summary>
    private sealed record CapturedRequest(string Uri, string? GoogApiKey, string? Authorization);

    /// <summary>
    /// Records every outgoing request's URL and auth headers, then returns a canned response. Tests the
    /// real wire format the client produces (default headers are merged into the request by HttpClient
    /// before it reaches this handler), without needing a live server.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _responder;
        public List<CapturedRequest> Captured { get; } = new();

        public CapturingHandler(Func<Uri, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? goog = request.Headers.TryGetValues("x-goog-api-key", out var g) ? string.Join(",", g) : null;
            string? auth = request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : null;
            Captured.Add(new CapturedRequest(request.RequestUri!.ToString(), goog, auth));
            return Task.FromResult(_responder(request.RequestUri!));
        }
    }
}
