// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// A stream has exactly one outcome. A cancelled stream is reported twice in practice: by the
/// read that observed the cancellation, and then by the consumer that saw the enumeration end
/// quietly. The second report used to throw "an attempt was made to transition a task to a final
/// state when it had already completed" out of the consumer, which surfaced on dev on 2026-09-04
/// as a name-generation runner failing on a user Stop.
/// </summary>
public sealed class StreamingMetadataTrackerTests
{
    /// <summary>The first outcome wins and later reports are ignored rather than thrown.</summary>
    [Fact]
    public void A_second_completion_is_ignored_not_thrown()
    {
        StreamingMetadataTracker tracker = new(DateTime.UtcNow);
        tracker.CompleteCanceled(new OperationCanceledException());

        Action again = tracker.CompleteSuccessfully;

        again.Should().NotThrow();
        tracker.IsCompleted.Should().BeTrue();
        tracker.CompletionTask.Result.IsSuccess.Should().BeFalse("the cancellation was the real outcome");
        tracker.CompletionTask.Result.Context.Should().Be("Streaming was canceled");
    }

    /// <summary>A stream that simply ran out is a success, reported once.</summary>
    [Fact]
    public void A_stream_that_ran_out_is_a_success()
    {
        StreamingMetadataTracker tracker = new(DateTime.UtcNow);
        tracker.IncrementChunkCount();
        tracker.IncrementChunkCount();

        tracker.CompleteSuccessfully();

        tracker.IsCompleted.Should().BeTrue();
        tracker.CompletionTask.Result.IsSuccess.Should().BeTrue();
        tracker.CompletionTask.Result.ChunkCount.Should().Be(2);
    }

    /// <summary>An error after a success does not rewrite history either.</summary>
    [Fact]
    public void An_error_after_success_is_ignored()
    {
        StreamingMetadataTracker tracker = new(DateTime.UtcNow);
        tracker.CompleteSuccessfully();

        Action late = () => tracker.CompleteWithError(new InvalidOperationException("late"));

        late.Should().NotThrow();
        tracker.CompletionTask.Result.IsSuccess.Should().BeTrue();
    }
}
