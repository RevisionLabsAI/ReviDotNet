// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests;

public class InactivityTimeoutTests
{
    private static (InferClient Client, TestServer Server) CreateClient(Protocol protocol, bool supportsCompletion, string defaultModel = "test-model", string apiKey = "test-key")
    {
        var (server, baseAddress) = FakeInferenceServer.Create();
        var httpClient = server.CreateClient();
        httpClient.BaseAddress = baseAddress; // set to TestServer base

        var client = new InferClient(
            apiUrl: baseAddress.ToString(),
            apiKey: apiKey,
            protocol: protocol,
            defaultModel: defaultModel,
            timeoutSeconds: 5,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            supportsCompletion: supportsCompletion,
            supportsGuidance: false,
            defaultGuidanceType: GuidanceType.Disabled,
            defaultGuidanceString: null,
            httpClientOverride: httpClient
        );

        return (client, server);
    }

    [Fact]
    public async Task NonStreaming_HeaderInactivity_TimesOut()
    {
        var (client, server) = CreateClient(Protocol.vLLM, supportsCompletion: true);
        using var _ = server; using var __ = client;
        Func<Task> act = async () => await client.GenerateAsync("__HANG_HEADERS__", model: "m1", inactivityTimeoutSeconds: 1);
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*headers*within 1*");
    }

    [Fact]
    public async Task NonStreaming_SlowBody_DoesNotTimeout()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, supportsCompletion: false);
        using var _ = server; using var __ = client;
        var msgs = new List<Message> { new("user", "__SLOW_BODY__") };
        var resp = await client.GenerateAsync(msgs, model: "gpt");
        resp.Selected.Should().Be("Hello world (chat)");
    }

    [Fact]
    public async Task Streaming_HeaderInactivity_TimesOut()
    {
        var (client, server) = CreateClient(Protocol.Gemini, supportsCompletion: true, defaultModel: "gemini-pro");
        using var _ = server; using var __ = client;
        var stream = client.GenerateStreamAsync("__STREAM_HANG_HEADERS__", model: "gemini-pro", inactivityTimeoutSeconds: 1);
        // Materialize by iterating to first item
        Func<Task> act = async () => { await foreach (var _chunk in stream.Stream.WithCancellation(CancellationToken.None)) { /* noop */ } };
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*streaming response headers*within 1*");
    }

    [Fact]
    public async Task Streaming_IdleMidStream_TimesOut()
    {
        var (client, server) = CreateClient(Protocol.Gemini, supportsCompletion: true, defaultModel: "gemini-pro");
        using var _ = server; using var __ = client;
        var stream = client.GenerateStreamAsync("__STREAM_IDLE__", model: "gemini-pro", inactivityTimeoutSeconds: 1);
        var chunks = new List<string>();
        Func<Task> act = async () =>
        {
            await foreach (var chunk in stream.Stream.WithCancellation(CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        };
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*No streaming data received*");
        chunks.Should().NotBeEmpty(); // at least first chunk arrived
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        var (client, server) = CreateClient(Protocol.vLLM, supportsCompletion: true);
        using var _ = server; using var __ = client;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100); // cancel before inactivity (1s)
        Func<Task> act = async () => await client.GenerateAsync("__HANG_HEADERS__", model: "m1", cancellationToken: cts.Token, inactivityTimeoutSeconds: 1);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
