// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Revi.Tests.Helpers;

/// <summary>
/// Reusable test fake for <see cref="IReviLogger"/>. Every log call is captured (level + final
/// message) into <see cref="Entries"/> for assertions, and a throwaway <see cref="Rlog"/> is
/// returned so production code that chains on the result keeps working. Nothing is written to the
/// console or to disk.
/// </summary>
/// <remarks>
/// Use the generic <see cref="RecordingReviLogger{T}"/> wherever a typed
/// <c>IReviLogger&lt;TCategory&gt;</c> is required — e.g. the registry manager service constructors
/// (<see cref="ProviderManagerService"/>, <see cref="ModelManagerService"/>,
/// <see cref="EmbeddingManagerService"/>).
/// </remarks>
internal class RecordingReviLogger : IReviLogger
{
    /// <summary>A single captured log call.</summary>
    public readonly record struct Entry(LogLevel Level, string Message);

    private readonly List<Entry> _entries = new();

    /// <summary>All captured log calls, in the order they were made.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    private Rlog Capture(LogLevel level, string message)
    {
        _entries.Add(new Entry(level, message));
        // Cheap, side-effect-free Rlog: the real ReviLogger builds one the same way.
        return new Rlog(null, level, message);
    }

    public Rlog LogInfo(string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Info, message);

    public Rlog LogInfo(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Info, message);

    public Rlog LogDebug(string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Debug, message);

    public Rlog LogDebug(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Debug, message);

    public Rlog LogWarning(string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Warning, message);

    public Rlog LogWarning(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Warning, message);

    public Rlog LogError(string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Error, message);

    public Rlog LogError(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Error, message);

    public Rlog LogFatal(string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Fatal, message);

    public Rlog LogFatal(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null,
        object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null,
        string? file = "", string? member = "", int? line = 0)
        => Capture(LogLevel.Fatal, message);

    public Rlog Log(Rlog? parent, LogLevel level, string message, string? identifier = "", int cycle = 0,
        string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null,
        string? object2Name = null, string? file = "", string? member = "", int? line = 0)
        => Capture(level, message);

    public Task DumpLog(StringBuilder sb, string fileNamePrefix) => Task.CompletedTask;

    public Task DumpLog(string? textToDump, string fileNamePrefix, Rlog? record = null) => Task.CompletedTask;

    public Task DumpImage(byte[] imageBytes, string fileNamePrefix, string extension = "png") => Task.CompletedTask;

    public bool IsEnabled(LogLevel level) => true;
}

/// <summary>
/// Typed variant of <see cref="RecordingReviLogger"/> satisfying <see cref="IReviLogger{T}"/>, so it
/// can be passed to the registry manager service constructors which take an
/// <c>IReviLogger&lt;TService&gt;</c>.
/// </summary>
/// <typeparam name="T">Category type (the service the logger is for).</typeparam>
internal sealed class RecordingReviLogger<T> : RecordingReviLogger, IReviLogger<T>
{
}
