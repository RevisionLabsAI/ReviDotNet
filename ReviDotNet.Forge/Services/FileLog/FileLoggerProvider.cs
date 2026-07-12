// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Text;

namespace ReviDotNet.Forge.Services.FileLog;

/// <summary>
/// Minimal rolling file logger for Forge — one file per day under a logs directory, so a crash or silent
/// process death leaves evidence (host lifecycle, campaign progress, memory stats) that survives the
/// process. Dependency-free by design: this is a diagnostics floor, not a logging framework.
/// <para>
/// Lines are queued to an unbounded in-memory queue and flushed by a single background writer, so logging
/// never blocks request/campaign threads on disk I/O. The queue is drained continuously and the writer
/// flushes after each batch; on dispose (host shutdown) it drains whatever remains. Volume is bounded by
/// the configured minimum level (default Information — ASP.NET request noise is filtered by category).
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Task _writer;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoop);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line)
    {
        // CompleteAdding raced with a late log line — drop it; the host is shutting down anyway.
        try { _queue.Add(line); }
        catch (InvalidOperationException) { }
    }

    private void WriteLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try
            {
                string path = Path.Combine(_directory, $"forge-{DateTime.Now:yyyyMMdd}.log");
                using StreamWriter sw = new(path, append: true, Encoding.UTF8);
                sw.WriteLine(line);
                // Drain whatever else is queued into the same open file before letting it close.
                while (_queue.TryTake(out string? more))
                    sw.WriteLine(more);
            }
            catch
            {
                // Disk trouble must never take down the host; lines are simply lost.
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Abbrev(logLevel)}] {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            provider.Enqueue(line);
        }

        private static string Abbrev(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
