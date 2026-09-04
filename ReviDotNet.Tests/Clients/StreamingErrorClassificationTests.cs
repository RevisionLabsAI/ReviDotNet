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
/// A provider that accepted a streaming request can still fail it after the 200 — a quota that
/// ran out between the connection and the first token, an overloaded backend — and reports that
/// as an error object on a data line. Before 2026-09-03 such a line parsed as a chunk with no
/// text and was dropped: the stream ended empty, nothing was thrown, and the provider monitor
/// never heard about it. These tests pin that an in-stream error is classified, reported and
/// thrown exactly like a failed connection.
/// </summary>
public sealed class StreamingErrorClassificationTests
{
    private const string OpenAiChunk =
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"index\":0}]}\n\n";

    private const string OpenAiBillingError =
        "data: {\"error\":{\"message\":\"You have no credits remaining.\",\"type\":\"insufficient_quota\",\"code\":\"credit_balance_exhausted\"}}\n\n";

    private const string AnthropicOverloadedEvent =
        "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

    /// <summary>An OpenAI-shaped billing error mid-stream is thrown as a classified provider exception and reported.</summary>
    [Fact]
    public async Task A_billing_error_inside_the_stream_is_classified_reported_and_thrown()
    {
        List<InferenceProviderOutcome> outcomes = [];
        Action<InferenceProviderOutcome> observer = outcomes.Add;
        InferenceProviderMonitor.OutcomeObserved += observer;
        try
        {
            using InferClient client = CreateClient(Protocol.OpenAI, OpenAiChunk + OpenAiBillingError);
            List<string> chunks = [];

            Func<Task> drain = async () =>
            {
                await foreach (string chunk in client.GenerateStreamAsync([new Message("user", "hi")], model: "gpt-x").Stream)
                    chunks.Add(chunk);
            };

            (await drain.Should().ThrowAsync<InferenceProviderException>())
                .Which.Failure.Kind.Should().Be(InferenceFailureKind.Billing);
            chunks.Should().ContainSingle().Which.Should().Be("Hello", "text before the error still streams through");

            InferenceProviderOutcome failure = outcomes.Should().ContainSingle(o => !o.Succeeded).Subject;
            failure.ProviderName.Should().Be("openai");
            failure.ModelName.Should().Be("gpt-x", "the outcome names the model the request asked for");
            failure.Streaming.Should().BeTrue();
            failure.Failure!.Kind.Should().Be(InferenceFailureKind.Billing);
            failure.Failure.ProviderCode.Should().Be("credit_balance_exhausted");
        }
        finally
        {
            InferenceProviderMonitor.OutcomeObserved -= observer;
        }
    }

    /// <summary>An Anthropic error event mid-stream classifies by its error type, here as transient overload.</summary>
    [Fact]
    public async Task An_anthropic_error_event_inside_the_stream_is_classified()
    {
        using InferClient client = CreateClient(Protocol.Claude, AnthropicOverloadedEvent);

        Func<Task> drain = async () =>
        {
            await foreach (string _ in client.GenerateStreamAsync([new Message("user", "hi")], model: "claude-x").Stream) { }
        };

        (await drain.Should().ThrowAsync<InferenceProviderException>())
            .Which.Failure.Kind.Should().Be(InferenceFailureKind.Transient);
    }

    /// <summary>A normal stream with no error object is unaffected by the check.</summary>
    [Fact]
    public async Task A_stream_without_an_error_object_is_untouched()
    {
        using InferClient client = CreateClient(Protocol.OpenAI, OpenAiChunk + OpenAiChunk + "data: [DONE]\n\n");
        List<string> chunks = [];

        await foreach (string chunk in client.GenerateStreamAsync([new Message("user", "hi")], model: "gpt-x").Stream)
            chunks.Add(chunk);

        chunks.Should().Equal("Hello", "Hello");
    }

    /// <summary>Builds a client whose only response is the given SSE body.</summary>
    /// <param name="protocol">The wire protocol.</param>
    /// <param name="sseBody">The full SSE response body.</param>
    /// <returns>The client.</returns>
    private static InferClient CreateClient(Protocol protocol, string sseBody)
    {
        CannedHandler handler = new(sseBody);
        HttpClient http = new(handler) { BaseAddress = new Uri("https://gen.example/") };
        return new InferClient(
            apiUrl: "https://gen.example/",
            apiKey: "test-key",
            protocol: protocol,
            defaultModel: "default-model",
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            supportsCompletion: true,
            httpClientOverride: http,
            providerName: "OpenAI");
    }

    /// <summary>Answers every request with the same 200 SSE body.</summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        /// <summary>The SSE body to return.</summary>
        private readonly string _body;

        /// <summary>Creates the handler.</summary>
        /// <param name="body">The SSE body to return.</param>
        public CannedHandler(string body) => _body = body;

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream")
            });
    }
}
