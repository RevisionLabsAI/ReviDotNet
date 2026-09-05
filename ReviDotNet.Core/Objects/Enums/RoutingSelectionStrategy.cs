// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Defines the primary attribute a gateway should use when ordering eligible upstreams.
/// </summary>
public enum RoutingSelectionStrategy
{
    /// <summary>Prefer the least expensive eligible upstream.</summary>
    LowestCost,

    /// <summary>Prefer the eligible upstream with the lowest observed latency.</summary>
    LowestLatency,

    /// <summary>Prefer the eligible upstream with the highest observed token throughput.</summary>
    HighestThroughput
}
