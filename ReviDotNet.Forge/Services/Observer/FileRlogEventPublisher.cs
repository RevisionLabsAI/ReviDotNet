// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Revi;

namespace ReviDotNet.Forge.Services.Observer;

/// <summary>
/// File-backed <see cref="IRlogEventPublisher"/>: every ReviLog event is appended as one JSON line to a
/// daily <c>revilog-yyyyMMdd.jsonl</c> file. Runs ALONGSIDE the Mongo sink (or alone when Mongo isn't
/// configured/reachable) so the full structured event stream — agent runs, LLM calls, campaign activity —
/// survives a process crash on local disk.
/// <para>
/// Events flow through a BOUNDED channel (drop-newest when full, so a dead disk can never balloon memory)
/// and a single writer drains continuously, flushing after every batch — an event is durable within
/// milliseconds of being logged, not at some future flush interval. Dropped-event counts are recorded in
/// the stream itself once writing recovers.
/// </para>
/// </summary>
public sealed class FileRlogEventPublisher : IRlogEventPublisher, IDisposable
{
    private const int Capacity = 50_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly Channel<RlogEvent> _channel;
    private readonly Task _writer;
    private long _dropped;

    public FileRlogEventPublisher(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _channel = Channel.CreateBounded<RlogEvent>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public Task PublishLogEventAsync(RlogEvent rlogEvent)
    {
        PublishLogEvent(rlogEvent);
        return Task.CompletedTask;
    }

    public void PublishLogEvent(RlogEvent rlogEvent)
    {
        if (!_channel.Writer.TryWrite(rlogEvent))
            Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync()
    {
        ChannelReader<RlogEvent> reader = _channel.Reader;
        while (await reader.WaitToReadAsync())
        {
            try
            {
                string path = Path.Combine(_directory, $"revilog-{DateTime.Now:yyyyMMdd}.jsonl");
                await using StreamWriter sw = new(path, append: true, Encoding.UTF8);

                long dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    await sw.WriteLineAsync($"{{\"level\":\"Warning\",\"message\":\"file sink dropped {dropped} events (channel full)\"}}");

                while (reader.TryRead(out RlogEvent? ev))
                {
                    string line;
                    try { line = JsonSerializer.Serialize(ev, JsonOptions); }
                    catch (Exception ex) { line = $"{{\"level\":\"Warning\",\"message\":\"unserializable event: {ex.GetType().Name}\"}}"; }
                    await sw.WriteLineAsync(line);
                }
                await sw.FlushAsync();
            }
            catch
            {
                // Disk trouble must never take down the host or spin hot; pause briefly and retry.
                await Task.Delay(1000);
            }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }
}

/// <summary>Fans one ReviLog event stream out to several sinks (e.g. Mongo + file). Sink faults are isolated.</summary>
public sealed class MultiRlogEventPublisher(IReadOnlyList<IRlogEventPublisher> sinks) : IRlogEventPublisher
{
    public async Task PublishLogEventAsync(RlogEvent rlogEvent)
    {
        foreach (IRlogEventPublisher sink in sinks)
        {
            try { await sink.PublishLogEventAsync(rlogEvent); }
            catch { /* one sink must not break the others */ }
        }
    }

    public void PublishLogEvent(RlogEvent rlogEvent)
    {
        foreach (IRlogEventPublisher sink in sinks)
        {
            try { sink.PublishLogEvent(rlogEvent); }
            catch { /* one sink must not break the others */ }
        }
    }
}
