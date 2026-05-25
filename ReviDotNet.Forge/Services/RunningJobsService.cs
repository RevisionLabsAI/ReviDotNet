// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace ReviDotNet.Forge.Services;

public enum JobKind { Test, Analyze, Generate, AgentRun, Evaluate, Revise }

/// <summary>
/// A single tracked in-flight job. Holds metadata for display + a cancel callback.
/// </summary>
public sealed class JobEntry
{
    public required string Id { get; init; }
    public required JobKind Kind { get; init; }
    public required string Title { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    /// <summary>If true, the job's start was requested but a worker hasn't acknowledged yet.</summary>
    public bool IsPending { get; set; }
    /// <summary>Cancel callback; safe to invoke even after completion.</summary>
    public required Action Cancel { get; init; }
}

/// <summary>
/// Process-wide registry of in-flight long-running jobs (tests, analyses, agent runs).
/// Surfaces them to the AppBar so users can see global activity and cancel individual jobs.
///
/// Pages call <see cref="Register"/> when they begin a long-running operation and
/// <see cref="Complete"/> when they finish (or in a finally block). The Changed event
/// fires on every mutation so the indicator can re-render.
/// </summary>
public sealed class RunningJobsService
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();

    /// <summary>Raised whenever a job is added or removed.</summary>
    public event Action? Changed;

    public IReadOnlyList<JobEntry> Snapshot()
        => _jobs.Values.OrderBy(j => j.StartedAt).ToList();

    public int Count => _jobs.Count;

    /// <summary>
    /// Register a new job and return its assigned id. Caller should keep the id and pass it
    /// to <see cref="Complete"/> in a finally block to ensure cleanup.
    /// </summary>
    public string Register(JobKind kind, string title, Action cancel)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        _jobs[id] = new JobEntry
        {
            Id = id,
            Kind = kind,
            Title = title,
            Cancel = cancel
        };
        Changed?.Invoke();
        return id;
    }

    /// <summary>Mark a job complete (also called from user-driven Cancel).</summary>
    public void Complete(string id)
    {
        if (_jobs.TryRemove(id, out _))
            Changed?.Invoke();
    }

    /// <summary>Cancel a job by id — invokes its cancel callback and removes it.</summary>
    public void CancelById(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            try { job.Cancel(); } catch { /* swallow — best-effort */ }
            Complete(id);
        }
    }
}
