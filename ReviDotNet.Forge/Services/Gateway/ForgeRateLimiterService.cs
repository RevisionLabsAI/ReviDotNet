// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace ReviDotNet.Forge.Services.Gateway;

public interface IForgeRateLimiterService
{
    void Configure(string providerName, int maxConcurrent, int delayMs);
    Task AcquireAsync(string providerName, CancellationToken cancellationToken = default);
    void Release(string providerName);
}

public class ForgeRateLimiterService : IForgeRateLimiterService
{
    private readonly ConcurrentDictionary<string, ProviderLimiter> _limiters = new();

    public void Configure(string providerName, int maxConcurrent, int delayMs)
    {
        _limiters[providerName] = new ProviderLimiter(
            Math.Max(1, maxConcurrent),
            Math.Max(0, delayMs));
    }

    public async Task AcquireAsync(string providerName, CancellationToken cancellationToken = default)
    {
        var limiter = _limiters.GetOrAdd(providerName, _ => new ProviderLimiter(10, 0));
        await limiter.Semaphore.WaitAsync(cancellationToken);
        if (limiter.DelayMs > 0)
            await Task.Delay(limiter.DelayMs, cancellationToken);
    }

    public void Release(string providerName)
    {
        if (_limiters.TryGetValue(providerName, out var limiter))
            limiter.Semaphore.Release();
    }

    private class ProviderLimiter(int maxConcurrent, int delayMs)
    {
        public SemaphoreSlim Semaphore { get; } = new(maxConcurrent, maxConcurrent);
        public int DelayMs { get; } = delayMs;
    }
}
