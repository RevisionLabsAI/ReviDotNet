// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Threading.Channels;
using MongoDB.Driver;
using Revi;
using ReviDotNet.Forge.Services.Mongo;

namespace ReviDotNet.Forge.Services.Observer;

/// <summary>
/// MongoDB-backed implementation of IRlogEventPublisher. Buffers events through an
/// unbounded channel and flushes in batches so logging never blocks the caller.
/// Writes into the same "LogEvents" collection that MongoReviLogViewerService reads from.
/// </summary>
public sealed class MongoRlogEventPublisher : IRlogEventPublisher, IAsyncDisposable
{
    private const int MaxBatchSize = 100;
    private const int FlushIntervalMs = 500;

    private readonly IMongoCollection<RlogEvent> _col;
    private readonly Channel<RlogEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _processor;

    public MongoRlogEventPublisher(IForgeMongoConnectionService mongo)
    {
        _col = mongo.GetCollection<RlogEvent>("LogEvents");
        _channel = Channel.CreateUnbounded<RlogEvent>(new UnboundedChannelOptions { SingleReader = true });
        _cts = new CancellationTokenSource();
        _processor = Task.Run(() => ProcessAsync(_cts.Token));
    }

    public Task PublishLogEventAsync(RlogEvent rlogEvent)
    {
        _channel.Writer.TryWrite(rlogEvent);
        return Task.CompletedTask;
    }

    public void PublishLogEvent(RlogEvent rlogEvent)
        => _channel.Writer.TryWrite(rlogEvent);

    private async Task ProcessAsync(CancellationToken ct)
    {
        var batch = new List<RlogEvent>(MaxBatchSize);
        var reader = _channel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxBatchSize && reader.TryRead(out var ev))
                    batch.Add(ev);

                if (batch.Count == 0) continue;

                try
                {
                    await _col.InsertManyAsync(batch, options: new InsertManyOptions { IsOrdered = false }, cancellationToken: ct);
                }
                catch
                {
                    // Never let a logging failure crash the host. Events are dropped silently.
                }

                try { await Task.Delay(FlushIntervalMs, ct); } catch { }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _processor; } catch { }
        _cts.Dispose();
    }
}
