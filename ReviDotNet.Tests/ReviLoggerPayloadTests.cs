// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Bounds on log-call object payload serialization.
/// </summary>
/// <remarks>
/// Payload serialization runs on the CALLING thread at every log call that passes an object. On
/// 2026-08-18 an unbounded payload — a live multi-megabyte object graph under concurrent mutation —
/// pinned twenty caller threads inside the logger at once and took the consuming application's core
/// pipeline down twice. These tests pin the three bounds that make that impossible: a depth cap, a
/// length cap with a visible truncation marker, and the guarantee that no payload, however hostile,
/// can throw out of the logger.
/// </remarks>
public class ReviLoggerPayloadTests
{
    /// <summary>A payload whose properties throw, standing in for objects mutated mid-enumeration.</summary>
    private sealed class HostilePayload
    {
        /// <summary>Always throws, as a mutating collection's enumerator would.</summary>
        public string Boom => throw new InvalidOperationException("Collection was modified");

        /// <summary>A survivable property, to show partial serialization still comes through.</summary>
        public string Fine => "still here";
    }

    /// <summary>A self-referencing payload, standing in for object graphs with back-references.</summary>
    private sealed class CyclicPayload
    {
        /// <summary>Points back at the instance itself.</summary>
        public CyclicPayload? Self { get; set; }
    }

    /// <summary>Null in, null out — an absent payload stays absent rather than becoming "null".</summary>
    [Fact]
    public void A_null_payload_stays_null()
    {
        ReviLogger.SerializePayloadBounded(null).Should().BeNull();
    }

    /// <summary>An oversized payload is truncated at the cap, and says so.</summary>
    [Fact]
    public void An_oversized_payload_is_truncated_with_a_marker()
    {
        List<string> huge = [];
        for (int i = 0; i < 20_000; i++)
        {
            huge.Add($"filler-{i}-abcdefghijklmnopqrstuvwxyz");
        }

        string? json = ReviLogger.SerializePayloadBounded(huge);

        json.Should().NotBeNull();
        json!.Length.Should().BeLessThan(70_000, "the cap is 64K characters plus a short marker");
        json.Should().Contain("[payload truncated:", "silent truncation would read as complete data");
    }

    /// <summary>A cyclic graph serializes rather than throwing or recursing.</summary>
    [Fact]
    public void A_cyclic_payload_does_not_throw()
    {
        CyclicPayload cyclic = new();
        cyclic.Self = cyclic;

        Action act = () => ReviLogger.SerializePayloadBounded(cyclic);

        act.Should().NotThrow();
    }

    /// <summary>
    /// A payload whose members throw — the shape a live object being mutated by other threads
    /// presents — still produces output and never lets the exception out of the logger.
    /// </summary>
    [Fact]
    public void A_hostile_payload_never_throws_out_of_the_logger()
    {
        string? json = null;
        Action act = () => json = ReviLogger.SerializePayloadBounded(new HostilePayload());

        act.Should().NotThrow("a log call must never take its caller down");
        json.Should().NotBeNull();
        json.Should().Contain("still here", "survivable members should still be captured");
    }
}
