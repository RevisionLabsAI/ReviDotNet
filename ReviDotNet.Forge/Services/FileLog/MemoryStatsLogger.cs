// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;

namespace ReviDotNet.Forge.Services.FileLog;

/// <summary>
/// Logs process memory statistics once a minute so any abnormal growth (or a silent process death) leaves
/// a trail in the file log: working set, private bytes, managed heap, and Gen2/LOH collection counts.
/// Motivated by an unattributed dotnet.exe RADAR_PRE_LEAK report during a long parallel Refinery campaign —
/// with this trail, the next incident is diagnosable from logs/forge-*.log alone.
/// </summary>
public sealed class MemoryStatsLogger(ILogger<MemoryStatsLogger> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using Process p = Process.GetCurrentProcess();
                logger.LogInformation(
                    "memory: workingSet={WorkingSetMb}MB private={PrivateMb}MB managedHeap={HeapMb}MB gen2={Gen2} threads={Threads}",
                    p.WorkingSet64 / (1024 * 1024),
                    p.PrivateMemorySize64 / (1024 * 1024),
                    GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024),
                    GC.CollectionCount(2),
                    p.Threads.Count);
            }
        }
        catch (OperationCanceledException) { /* host shutdown */ }
    }
}
