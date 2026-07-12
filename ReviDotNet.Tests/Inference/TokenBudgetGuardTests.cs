// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Inference;

/// <summary>
/// The token-budget guard reconciles the context window (context-window), the model's real output
/// capability (output-capacity), and the requested output ceiling (output-budget) so a request that
/// would draw an opaque provider 400 is clamped to the largest budget that actually fits.
/// </summary>
public class TokenBudgetGuardTests
{
    private static ModelProfile Model(int contextWindow = 0, int? outputCapacity = null) => new()
    {
        Name = "test-model",
        ContextWindow = contextWindow,
        OutputCapacity = outputCapacity
    };

    [Fact]
    public void Null_request_passes_through()
    {
        TokenBudgetGuard.Clamp(null, 1000, Model(contextWindow: 8000, outputCapacity: 4000), "p").Should().BeNull();
    }

    [Fact]
    public void Request_within_all_limits_is_untouched()
    {
        TokenBudgetGuard.Clamp(4096, 1000, Model(contextWindow: 200_000, outputCapacity: 64_000), "p").Should().Be(4096);
    }

    [Fact]
    public void Request_above_output_capability_is_clamped_to_capability()
    {
        TokenBudgetGuard.Clamp(200_000, 1000, Model(contextWindow: 1_000_000, outputCapacity: 128_000), "p")
            .Should().Be(128_000, "the model cannot produce more than its output-capacity");
    }

    [Fact]
    public void Request_overflowing_the_context_window_is_clamped_to_the_remaining_room()
    {
        TokenBudgetGuard.Clamp(64_000, 190_000, Model(contextWindow: 200_000, outputCapacity: 64_000), "p")
            .Should().Be(10_000, "input + output must fit the context window");
    }

    [Fact]
    public void Both_clamps_compose_capability_first()
    {
        // Requested 500k → capability clamps to 128k → context room (1M − 950k) clamps to 50k.
        TokenBudgetGuard.Clamp(500_000, 950_000, Model(contextWindow: 1_000_000, outputCapacity: 128_000), "p")
            .Should().Be(50_000);
    }

    [Fact]
    public void No_declared_limits_means_no_clamping()
    {
        TokenBudgetGuard.Clamp(999_999, 5_000_000, Model(contextWindow: 0, outputCapacity: null), "p")
            .Should().Be(999_999, "context-window 0 means no context size is declared; nothing to clamp against");
    }

    [Fact]
    public void Context_clamp_never_goes_below_one()
    {
        TokenBudgetGuard.Clamp(4096, 250_000, Model(contextWindow: 200_000), "p")
            .Should().Be(1, "an already-overflowing input clamps the output budget to the 1-token floor rather than negative");
    }
}
