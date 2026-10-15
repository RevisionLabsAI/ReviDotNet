// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// The campaign dollar cap (<see cref="CampaignSpec.MaxCostUsd"/>): what it counts, when it stops a campaign,
/// and that a baseline-only campaign is bounded by it.
/// </summary>
public class CostCapTests
{
    // ── The governor on its own ─────────────────────────────────────────────────────

    [Fact]
    public void Without_a_cap_every_run_may_start()
    {
        CostGovernor cost = new(null, () => 1_000m);

        cost.TryStartRun().Should().BeTrue();
        cost.Stopped.Should().BeFalse();
    }

    [Fact]
    public void A_run_starts_only_while_the_largest_run_so_far_still_fits()
    {
        decimal meta = 0m;
        CostGovernor cost = new(2.50m, () => meta);

        cost.TryStartRun().Should().BeTrue("nothing is known yet and nothing is spent");
        cost.RecordRun(agentCost: 0.40m, metaCost: 0.60m);
        meta += 0.60m;

        cost.TryStartRun().Should().BeTrue("$1.00 spent plus a $1.00 run is within $2.50");
        cost.RecordRun(agentCost: 0.40m, metaCost: 0.60m);
        meta += 0.60m;

        cost.Spent.Should().Be(2.00m);
        cost.TryStartRun().Should().BeFalse("$2.00 spent plus a $1.00 run would cross $2.50");
        cost.Stopped.Should().BeTrue();
        cost.Describe().Should().Contain("$2.50");
    }

    [Fact]
    public void Nothing_starts_once_the_cap_is_spent()
    {
        CostGovernor cost = new(1m, () => 1m);

        cost.TryStartRun().Should().BeFalse();
    }

    // ── Pricing meta calls ──────────────────────────────────────────────────────────

    [Fact]
    public void A_meta_call_on_an_unpriced_model_is_charged_at_the_high_fallback_rate()
    {
        MetaLlmUsageBroker broker = new();

        decimal price = broker.Price(new CompletionResult { Model = "unknown", InputTokens = 1_000_000, OutputTokens = 100_000 });

        price.Should().Be(MetaLlmUsageBroker.FallbackCostPerMillionInputTokens + MetaLlmUsageBroker.FallbackCostPerMillionOutputTokens / 10m);
    }

    [Fact]
    public void Meta_spend_is_accumulated_in_dollars_inside_a_scope_only()
    {
        MetaLlmUsageBroker broker = new();
        CompletionResult call = new() { InputTokens = 100_000, OutputTokens = 0 };

        broker.Record(call);
        broker.SpentUsd.Should().Be(0m, "outside a scope nothing is recorded");

        using (broker.BeginScope())
        {
            broker.Record(call);
            broker.Record(call);
            broker.SpentUsd.Should().Be(2m, "two calls of 100,000 input tokens at the $10 fallback rate");
        }
    }
}
