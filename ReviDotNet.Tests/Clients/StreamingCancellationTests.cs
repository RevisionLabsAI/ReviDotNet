// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
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
/// A cancelled stream throws <see cref="OperationCanceledException"/> out of its enumeration. It
/// used to end quietly, which made "the caller stopped this" indistinguishable from "the model had
/// nothing to say": a consumer with a model fallback moved on to the next model with a dead token
/// (BetterNamer, 2026-09-04). The stream here delivers one chunk and then blocks until the reader
/// is cancelled, which is exactly a user pressing Stop while the model is still thinking.
/// </summary>
public sealed class StreamingCancellationTests
{
    private const string FirstChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"index\":0}]}\n\n";

    /// <summary>Cancelling mid-stream surfaces as cancellation, after what was already received.</summary>
    [Fact]
    public async Task A_cancelled_stream_throws_rather_than_ending_quietly()
    {
        using CancellationTokenSource cancellation = new();
        using InferClient client = CreateClient();
        List<string> chunks = [];

        Func<Task> drain = async () =>
        {
            await foreach (string chunk in client.GenerateStreamAsync([new Message("user", "hi")], model: "gpt-x", cancellationToken: cancellation.Token).Stream)
            {
                chunks.Add(chunk);
                cancellation.Cancel(); // Stop, pressed after the first token
            }
        };

        await drain.Should().ThrowAsync<OperationCanceledException>();
        chunks.Should().Equal("Hello"); // output received before the Stop is still delivered
    }

    /// <summary>The completion metadata records a cancellation, not a provider error.</summary>
    [Fact]
    public async Task The_completion_records_a_cancellation_not_an_error()
    {
        using CancellationTokenSource cancellation = new();
        using InferClient client = CreateClient();
        StreamingResult<string> result = client.GenerateStreamAsync([new Message("user", "hi")], model: "gpt-x", cancellationToken: cancellation.Token);

        try
        {
            await foreach (string _ in result.Stream)
                cancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        StreamingMetadata metadata = await result.Completion;
        metadata.IsSuccess.Should().BeFalse();
        metadata.Exception.Should().BeOfType<OperationCanceledException>();
        metadata.ChunkCount.Should().Be(1);
    }

    /// <summary>Builds a client over a stream that sends one chunk and then waits to be cancelled.</summary>
    /// <returns>The client.</returns>
    private static InferClient CreateClient()
    {
        HttpClient http = new(new HangingSseHandler()) { BaseAddress = new Uri("https://gen.example/") };
        return new InferClient(
            apiUrl: "https://gen.example/",
            apiKey: "test-key",
            protocol: Protocol.OpenAI,
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

    /// <summary>Answers every request with a 200 whose body is one SSE chunk followed by silence.</summary>
    private sealed class HangingSseHandler : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new HangingContent(FirstChunk)
            });
    }

    /// <summary>Content whose read stream yields its bytes once and then blocks until the reader is cancelled.</summary>
    private sealed class HangingContent : HttpContent
    {
        /// <summary>The bytes delivered before the hang.</summary>
        private readonly byte[] _first;

        /// <summary>Creates the content.</summary>
        /// <param name="first">The text delivered before the hang.</param>
        public HangingContent(string first)
        {
            _first = Encoding.UTF8.GetBytes(first);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        }

        /// <inheritdoc />
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new HangingStream(_first));

        /// <inheritdoc />
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();

        /// <inheritdoc />
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_first, 0, _first.Length);

        /// <inheritdoc />
        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>A read-only stream: the first read returns the payload, every later read waits for cancellation.</summary>
    private sealed class HangingStream : Stream
    {
        /// <summary>The payload.</summary>
        private readonly byte[] _payload;

        /// <summary>Whether the payload was handed out.</summary>
        private bool _delivered;

        /// <summary>Creates the stream.</summary>
        /// <param name="payload">The bytes to deliver once.</param>
        public HangingStream(byte[] payload) => _payload = payload;

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delivered)
            {
                _delivered = true;
                _payload.CopyTo(buffer);
                return _payload.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
