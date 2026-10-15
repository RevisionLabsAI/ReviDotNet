// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// <see cref="ProviderHaltWatch"/>: which provider failures stop a campaign, and that only failures raised in
/// the campaign's own async flow count.
/// </summary>
public class ProviderHaltWatchTests
{
    private static InferenceFailure Failure(InferenceFailureKind kind) => new(kind, 429, "code", "message");

    [Theory]
    [InlineData(InferenceFailureKind.Billing, true)]
    [InlineData(InferenceFailureKind.Authentication, true)]
    [InlineData(InferenceFailureKind.RateLimited, false)]
    [InlineData(InferenceFailureKind.Transient, false)]
    [InlineData(InferenceFailureKind.ModelUnavailable, false)]
    [InlineData(InferenceFailureKind.RequestInvalid, false)]
    public void Only_billing_and_key_failures_stop_the_campaign(InferenceFailureKind kind, bool stops)
    {
        using ProviderHaltWatch watch = new();

        InferenceProviderMonitor.ReportFailure("openai", "gpt-test", Failure(kind), streaming: false);

        watch.Halted.Should().Be(stops);
    }

    [Fact]
    public void A_halted_watch_throws_naming_the_provider_and_the_failure()
    {
        using ProviderHaltWatch watch = new();
        InferenceProviderMonitor.ReportFailure("OpenAI", "gpt-test", new InferenceFailure(InferenceFailureKind.Billing, 429, "insufficient_quota", "x"), streaming: true);

        Action act = watch.ThrowIfHalted;

        act.Should().Throw<ProviderHaltException>()
            .Which.Message.Should().Contain("'openai' (gpt-test)").And.Contain("Billing (429 insufficient_quota)");
    }

    [Fact]
    public async Task A_failure_in_another_async_flow_does_not_stop_this_campaign()
    {
        using ProviderHaltWatch watch = new();

        Task other;
        using (ExecutionContext.SuppressFlow())
            other = Task.Run(() => InferenceProviderMonitor.ReportFailure("openai", null, Failure(InferenceFailureKind.Billing), streaming: false));
        await other;

        watch.Halted.Should().BeFalse();
    }

    [Fact]
    public async Task A_failure_in_a_child_task_of_the_campaign_counts()
    {
        using ProviderHaltWatch watch = new();

        await Task.Run(() => InferenceProviderMonitor.ReportFailure("anthropic", null, Failure(InferenceFailureKind.Authentication), streaming: false));

        watch.Halted.Should().BeTrue();
    }

    [Fact]
    public void Disposing_stops_listening_and_clears_the_current_watch()
    {
        ProviderHaltWatch watch = new();
        ProviderHaltWatch.Current.Should().BeSameAs(watch);

        watch.Dispose();
        InferenceProviderMonitor.ReportFailure("openai", null, Failure(InferenceFailureKind.Billing), streaming: false);

        watch.Halted.Should().BeFalse();
        ProviderHaltWatch.Current.Should().BeNull();
    }
}
