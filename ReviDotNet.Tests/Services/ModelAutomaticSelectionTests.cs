// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Services;

public sealed class ModelAutomaticSelectionTests
{
    [Fact]
    public void Test_only_model_is_explicitly_addressable_but_never_used_as_tier_fallback()
    {
        ProviderManagerService providers = new(new RecordingReviLogger<ProviderManagerService>());
        ModelManagerService models = new(providers, new RecordingReviLogger<ModelManagerService>());
        models.Add(new ModelProfile
        {
            Name = "staged",
            Enabled = true,
            AllowAutomaticSelection = false,
            Tier = ModelTier.C
        });
        models.Add(new ModelProfile
        {
            Name = "production",
            Enabled = true,
            AllowAutomaticSelection = true,
            Tier = ModelTier.B
        });

        models.Get("staged").Should().NotBeNull("admin experiments select models explicitly by name");
        models.Find(ModelTier.C).Should().NotBeNull().And.Match<ModelProfile>(m => m.Name == "production",
            "automatic routing must skip staged/test-only profiles even when they have a cheaper tier");
    }

    [Fact]
    public void Automatic_selection_defaults_to_enabled_for_existing_profiles()
    {
        new ModelProfile().AllowAutomaticSelection.Should().BeTrue();
    }

    [Fact]
    public void Staged_model_config_parses_routing_sampling_and_thinking_controls()
    {
        const string config = """
            [[general]]
            name = staged-gemini
            enabled = true
            allow-automatic-selection = false
            model-string = gemini-3.7-flash
            provider-name = gemini

            [[settings]]
            tier = B
            thinking = low
            thinking-conversion-minimal = low

            [[override-settings]]
            best-of = disabled

            [[override-tuning]]
            temperature = disabled
            top-k = disabled
            top-p = disabled
            """;

        ModelProfile model = RConfigParser.ToObject<ModelProfile>(RConfigParser.ReadEmbedded(config))!;

        model.Enabled.Should().BeTrue();
        model.AllowAutomaticSelection.Should().BeFalse();
        model.ResolveThinking("minimal").Should().Be("low");
        model.Temperature.Should().Be("disabled");
        model.TopP.Should().Be("disabled");
        model.TopK.Should().Be("disabled");
        model.BestOf.Should().Be("disabled");
    }
}
