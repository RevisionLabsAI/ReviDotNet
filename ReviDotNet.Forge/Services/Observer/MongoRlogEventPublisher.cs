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
/// MongoDB-backed implementation of IRlogEventPublisher. Buffers events through a BOUNDED channel and
/// flushes in batches so logging never blocks the caller — and an unreachable Mongo can never balloon
/// process memory (events beyond the buffer are dropped, newest first). After an insert failure the sink
/// opens a circuit for <see cref="CircuitCooldownMs"/> and drops batches without attempting the (slow,
/// server-selection-timeout-bound) insert, so a dead database costs neither memory nor drain throughput.
/// Writes into the same "LogEvents" collection that MongoReviLogViewerService reads from.
/// </summary>
public sealed class MongoRlogEventPublisher : IRlogEventPublisher, IAsyncDisposable
{
    private const int MaxBatchSize = 100;
    private const int FlushIntervalMs = 500;
    private const int Capacity = 50_000;
    private const int CircuitCooldownMs = 30_000;

    private readonly IMongoCollection<RlogEvent> _col;
    private readonly Channel<RlogEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _processor;

    public MongoRlogEventPublisher(IForgeMongoConnectionService mongo)
    {
        _col = mongo.GetCollection<RlogEvent>("LogEvents");
        _channel = Channel.CreateBounded<RlogEvent>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
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
        var sinceFailure = new System.Diagnostics.Stopwatch();

        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxBatchSize && reader.TryRead(out var ev))
                    batch.Add(ev);

                if (batch.Count == 0) continue;

                // Circuit open: Mongo failed recently — drop the batch immediately instead of paying the
                // server-selection timeout again (which would throttle the drain to a crawl while the
                // channel keeps filling).
                if (sinceFailure.IsRunning && sinceFailure.ElapsedMilliseconds < CircuitCooldownMs)
                    continue;

                try
                {
                    await _col.InsertManyAsync(batch, options: new InsertManyOptions { IsOrdered = false }, cancellationToken: ct);
                    sinceFailure.Reset();
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Never let a logging failure crash the host. Events are dropped; retry after cooldown.
                    sinceFailure.Restart();
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
