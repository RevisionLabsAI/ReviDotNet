// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Verifies the proxy round-robin arithmetic in <see cref="BrowserService"/> — the part that can be
/// checked without launching a real browser.
///
/// <para>
/// The rotation counter is incremented on every request and never reset, so a long-lived scraping
/// process eventually pushes it past <see cref="int.MaxValue"/> and it wraps to
/// <see cref="int.MinValue"/>. C# <c>%</c> keeps the dividend's sign, so indexing with the raw counter
/// then produces a negative index and every rotation throws — and keeps throwing, because the counter
/// needs another ~2 billion increments to reach zero again. Nothing about that is browser-specific;
/// it is pure arithmetic, and it is the whole failure.
/// </para>
/// </summary>
public class BrowserProxyRotationTests
{
    /// <summary>An index is produced for every counter value, including the ones that used to throw.</summary>
    /// <param name="counter">A rotation counter value.</param>
    [Theory]
    [InlineData(int.MinValue)]  // the wrap point; Math.Abs cannot even represent its negation
    [InlineData(-2147483647)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void RotationIndex_is_in_range_for_any_counter(int counter)
    {
        foreach (int count in new[] { 1, 2, 3, 8 })
        {
            int index = BrowserService.RotationIndex(counter, count);

            index.Should().BeInRange(0, count - 1,
                $"counter {counter} over {count} proxies must select a real proxy, not throw");
        }
    }

    /// <summary>
    /// The guard must not cost the rotation its purpose: a full sweep from any starting point still
    /// visits every proxy exactly once, so load stays spread instead of pinning to one.
    /// </summary>
    /// <param name="start">The counter value the sweep starts from.</param>
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue - 2)]
    public void RotationIndex_sweeps_every_proxy_exactly_once(int start)
    {
        const int Count = 4;

        List<int> visited = Enumerable.Range(0, Count)
            .Select(i => BrowserService.RotationIndex(start + i, Count))
            .ToList();

        visited.Should().BeEquivalentTo(Enumerable.Range(0, Count),
            "GetRotationOrder walks i = 0..count-1 and must reach each proxy once");
    }
}
