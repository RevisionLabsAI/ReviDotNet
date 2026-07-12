// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace ReviDotNet.Forge.Services.FileLog;

/// <summary>
/// Last-resort crash evidence: writes SYNCHRONOUSLY (no queue, no background writer — the process may be
/// milliseconds from death) to <c>forge-crash.log</c> in the log directory. Wired to
/// AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException in Program.cs so a Forge or
/// Refinery crash always leaves a stack trace on disk, even when the async file log never gets to flush.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();
    private static string? _directory;

    /// <summary>Arms the crash handlers. Call once at startup, after the log directory is known.</summary>
    public static void Arm(string directory)
    {
        _directory = directory;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : ""),
                  e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved(); // evidence recorded; don't escalate a background fault into anything worse
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Write("ProcessExit (clean shutdown)", null);
    }

    /// <summary>Appends one crash record; safe to call from any thread, never throws.</summary>
    public static void Write(string source, Exception? exception)
    {
        if (_directory is null) return;
        try
        {
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [CRASH] {source}" +
                (exception is null ? "" : Environment.NewLine + exception) +
                Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, "forge-crash.log"), line);
            }
        }
        catch
        {
            // Nothing left to do — never throw from a crash handler.
        }
    }
}
