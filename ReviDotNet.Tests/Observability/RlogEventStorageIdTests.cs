// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Observability;

/// <summary>
/// Pins that every event ReviLogger publishes carries an Id a Mongo sink can store.
/// </summary>
/// <remarks>
/// <see cref="RlogEvent.Id"/> is mapped <c>[BsonRepresentation(BsonType.ObjectId)]</c>, so a GUID cannot
/// be serialized. The dump path (run on every inference call) used to publish GUID Ids; a sink that
/// inserts in batches then lost the whole batch around each one. On BetterNamer production, no dump
/// event had ever been stored, and a search-submission error vanished along with its neighbours.
/// </remarks>
public sealed class RlogEventStorageIdTests
{
    /// <summary>The hazard itself: a GUID Id does not serialize under the ObjectId mapping.</summary>
    [Fact]
    public void A_guid_id_cannot_be_serialized_as_an_rlog_event()
    {
        RlogEvent rlogEvent = new() { Id = Guid.NewGuid().ToString(), Message = "x" };

        Action serialize = () => rlogEvent.ToBson();

        serialize.Should().Throw<Exception>("RlogEvent.Id is mapped as an ObjectId");
    }

    /// <summary>
    /// A dump with no record behind it — the form every inference call uses — publishes an
    /// ObjectId Id, so the event can be stored and does not poison its batch.
    /// </summary>
    [Fact]
    public async Task A_dump_without_a_record_publishes_a_storable_id()
    {
        CapturingPublisher publisher = new();
        ReviLogger logger = new(publisher, new ConfigurationBuilder().Build());

        await logger.DumpLog("inference dump body", "rlog-storage-id-test");

        RlogEvent published = publisher.Events.Should().ContainSingle().Subject;
        ObjectId.TryParse(published.Id, out _).Should().BeTrue("a Mongo sink maps RlogEvent.Id as an ObjectId");
        Action serialize = () => published.ToBson();
        serialize.Should().NotThrow();
    }

    /// <summary>An ordinary log call already publishes an ObjectId; pinned so the two paths stay alike.</summary>
    [Fact]
    public void An_ordinary_log_call_publishes_a_storable_id()
    {
        CapturingPublisher publisher = new();
        ReviLogger logger = new(publisher, new ConfigurationBuilder().Build());

        logger.LogError("Error in submit search: boom", object1: new InvalidOperationException("boom"));

        RlogEvent published = publisher.Events.Should().ContainSingle().Subject;
        ObjectId.TryParse(published.Id, out _).Should().BeTrue();
        Action serialize = () => published.ToBson();
        serialize.Should().NotThrow();
    }

    /// <summary>Records every published event.</summary>
    private sealed class CapturingPublisher : IRlogEventPublisher
    {
        /// <summary>The events published, in order.</summary>
        public List<RlogEvent> Events { get; } = [];

        /// <inheritdoc />
        public void PublishLogEvent(RlogEvent rlogEvent) => Events.Add(rlogEvent);

        /// <inheritdoc />
        public Task PublishLogEventAsync(RlogEvent rlogEvent)
        {
            Events.Add(rlogEvent);
            return Task.CompletedTask;
        }
    }
}
