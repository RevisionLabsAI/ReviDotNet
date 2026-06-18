// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 5 audit fixes (D42 model-level prompt-completion override).
/// </summary>
public class Batch5FixesTests
{
    // ── D42: model-level supports-prompt-completion overrides the provider default ──

    [Fact]
    public void EffectiveSupportsPromptCompletion_ModelTrue_OverridesProviderFalse()
    {
        var model = new ModelProfile
        {
            SupportsPromptCompletion = true,
            Provider = new ProviderProfile { SupportsCompletion = false }
        };

        model.EffectiveSupportsPromptCompletion.Should().BeTrue();
    }

    [Fact]
    public void EffectiveSupportsPromptCompletion_ModelFalse_OverridesProviderTrue()
    {
        var model = new ModelProfile
        {
            SupportsPromptCompletion = false,
            Provider = new ProviderProfile { SupportsCompletion = true }
        };

        model.EffectiveSupportsPromptCompletion.Should().BeFalse();
    }

    [Fact]
    public void EffectiveSupportsPromptCompletion_ModelUnset_FallsBackToProvider()
    {
        new ModelProfile { SupportsPromptCompletion = null, Provider = new ProviderProfile { SupportsCompletion = true } }
            .EffectiveSupportsPromptCompletion.Should().BeTrue();

        new ModelProfile { SupportsPromptCompletion = null, Provider = new ProviderProfile { SupportsCompletion = false } }
            .EffectiveSupportsPromptCompletion.Should().BeFalse();
    }

    [Fact]
    public void EffectiveSupportsPromptCompletion_NoProvider_IsFalse()
    {
        new ModelProfile { SupportsPromptCompletion = null, Provider = null }
            .EffectiveSupportsPromptCompletion.Should().BeFalse();
    }
}
