// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying,
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
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
/// Pins that a Gemini call's provider outcome names the model the request asked for. The Gemini
/// payload transform drops "model" (Gemini takes it from the URL), and both clients used to read
/// the model after that transform, so every Gemini call was reported under the provider's default
/// model: BetterNamer's per-model call counts credited gemini-2.5-flash-lite calls to
/// gemini-2.5-flash.
/// </summary>
public sealed class GeminiOutcomeModelNameTests
{
    /// <summary>A unique provider name, so outcomes from tests running in parallel are not counted.</summary>
    private const string ProviderName = "gemini-outcome-label-test";

    private const string GeminiChatJson =
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}";

    private const string GeminiSse =
        "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"chunk\"}]},\"finishReason\":\"STOP\"}]}\n\n";

    /// <summary>A non-streaming Gemini call reports the requested model, not the provider default.</summary>
    [Fact]
    public async Task A_non_streaming_Gemini_call_reports_the_requested_model()
    {
        List<InferenceProviderOutcome> outcomes = await ObserveAsync(
            GeminiChatJson,
            "application/json",
            client => client.GenerateAsync([new Message("user", "hi")], model: "gemini-lite-x"));

        outcomes.Should().ContainSingle().Which.ModelName.Should().Be("gemini-lite-x");
    }

    /// <summary>A streaming Gemini call reports the requested model, not the provider default.</summary>
    [Fact]
    public async Task A_streaming_Gemini_call_reports_the_requested_model()
    {
        List<InferenceProviderOutcome> outcomes = await ObserveAsync(
            GeminiSse,
            "text/event-stream",
            async client =>
            {
                await foreach (string _ in client.GenerateStreamAsync([new Message("user", "hi")], model: "gemini-lite-x").Stream)
                {
                    // drain
                }
            });

        InferenceProviderOutcome outcome = outcomes.Should().ContainSingle().Subject;
        outcome.ModelName.Should().Be("gemini-lite-x");
        outcome.Streaming.Should().BeTrue();
    }

    /// <summary>Runs one call against a canned response and returns the outcomes it reported.</summary>
    /// <param name="body">The response body.</param>
    /// <param name="mediaType">The response content type.</param>
    /// <param name="call">The call to make.</param>
    /// <returns>This test's outcomes.</returns>
    private static async Task<List<InferenceProviderOutcome>> ObserveAsync(
        string body, string mediaType, Func<InferClient, Task> call)
    {
        List<InferenceProviderOutcome> outcomes = [];
        Action<InferenceProviderOutcome> observer = o =>
        {
            if (string.Equals(o.ProviderName, ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                lock (outcomes)
                {
                    outcomes.Add(o);
                }
            }
        };

        InferenceProviderMonitor.OutcomeObserved += observer;
        try
        {
            using HttpClient http = new(new CannedHandler(body, mediaType)) { BaseAddress = new Uri("https://gen.example/") };
            using InferClient client = new(
                apiUrl: "https://gen.example/",
                apiKey: "test-key",
                protocol: Protocol.Gemini,
                defaultModel: "gemini-default",
                timeoutSeconds: 30,
                delayBetweenRequestsMs: 0,
                retryAttemptLimit: 1,
                retryInitialDelaySeconds: 0,
                simultaneousRequests: 2,
                supportsCompletion: true,
                httpClientOverride: http,
                providerName: ProviderName);

            await call(client);
        }
        finally
        {
            InferenceProviderMonitor.OutcomeObserved -= observer;
        }

        lock (outcomes)
        {
            return outcomes.ToList();
        }
    }

    /// <summary>Answers every request with the same 200 response.</summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        /// <summary>The response body.</summary>
        private readonly string _body;

        /// <summary>The response content type.</summary>
        private readonly string _mediaType;

        /// <summary>Creates the handler.</summary>
        /// <param name="body">The response body.</param>
        /// <param name="mediaType">The response content type.</param>
        public CannedHandler(string body, string mediaType)
        {
            _body = body;
            _mediaType = mediaType;
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_body, Encoding.UTF8, _mediaType)
            });
    }
}
