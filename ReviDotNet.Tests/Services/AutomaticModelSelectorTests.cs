// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// Pins automatic model selection with provider availability: unchanged when every provider is up,
/// and skipping an out-of-service provider to the next preferred or same-tier model when one is down.
/// The case that motivated it: a Gemini prepaid account ran out on 2026-11-06 and every prompt kept
/// resolving to Gemini for two days while other providers were healthy.
/// </summary>
public sealed class AutomaticModelSelectorTests
{
    /// <summary>A fake registry: models in tier order, with Find honouring the blocked list.</summary>
    private sealed class Registry
    {
        private readonly List<ModelProfile> _models;

        public Registry(params ModelProfile[] models) => _models = [.. models];

        public ModelProfile? Get(string name) => _models.FirstOrDefault(m => m.Name == name);

        public ModelProfile? Find(ModelTier minTier, List<string> blocked) => _models
            .Where(m => m.Enabled && m.Tier >= minTier && !blocked.Contains(m.Name))
            .MinBy(m => m.Tier);

        /// <summary>How many tier lookups were made, to prove candidates are produced lazily.</summary>
        public int FindCalls { get; private set; }

        public ModelProfile? CountingFind(ModelTier minTier, List<string> blocked)
        {
            FindCalls++;
            return Find(minTier, blocked);
        }
    }

    private static ModelProfile Model(string name, string provider, ModelTier tier, bool enabled = true) => new()
    {
        Name = name,
        ProviderName = provider,
        Provider = new ProviderProfile { Name = provider },
        Tier = tier,
        Enabled = enabled
    };

    private static ModelProfile? Select(
        Registry registry,
        IReadOnlyList<string>? preferred,
        Func<string, bool>? usable,
        IReadOnlyCollection<string>? excluded = null,
        List<string>? log = null)
        => AutomaticModelSelector.Select(
            "test-prompt",
            preferred,
            registry.Get,
            blocked => registry.CountingFind(ModelTier.B, blocked),
            blocked => registry.Find(ModelTier.C, blocked),
            null,
            usable,
            excluded,
            line => log?.Add(line));

    /// <summary>With no availability source, selection is exactly the old rule: first enabled preference.</summary>
    [Fact]
    public void Without_availability_the_first_enabled_preference_wins()
    {
        Registry registry = new(Model("gemini_flash", "gemini", ModelTier.B), Model("claude_haiku", "claude", ModelTier.B));

        Select(registry, ["gemini_flash", "claude_haiku"], usable: null)!.Name.Should().Be("gemini_flash");
    }

    /// <summary>A preferred model on an out-of-service provider gives way to the next preference.</summary>
    [Fact]
    public void An_out_of_service_preference_gives_way_to_the_next()
    {
        Registry registry = new(Model("gemini_flash", "gemini", ModelTier.B), Model("claude_haiku", "claude", ModelTier.B));
        List<string> log = [];

        ModelProfile? selected = Select(registry, ["gemini_flash", "claude_haiku"], usable: p => p != "gemini", log: log);

        selected!.Name.Should().Be("claude_haiku");
        log.Should().Contain(l => l.Contains("gemini_flash") && l.Contains("out of service"));
    }

    /// <summary>
    /// The production shape: no preferences, tier selection lands on Gemini first. With Gemini out it
    /// must move to the next model in the same tier rather than keep resolving there.
    /// </summary>
    [Fact]
    public void Tier_selection_moves_to_the_next_model_in_the_tier()
    {
        Registry registry = new(
            Model("gemini_flash", "gemini", ModelTier.B),
            Model("gpt_mini", "openai", ModelTier.B),
            Model("groq_oss", "groq", ModelTier.C));

        Select(registry, preferred: null, usable: p => p != "gemini")!.Name.Should().Be("gpt_mini");
    }

    /// <summary>When the whole tier is down, the tier-C fallback is tried before giving up.</summary>
    [Fact]
    public void A_fully_down_tier_falls_back_to_tier_c()
    {
        Registry registry = new(
            Model("gemini_flash", "gemini", ModelTier.B),
            Model("groq_oss", "groq", ModelTier.C));

        // Tier lookups use tier B as the minimum, so groq (tier C) is only reachable via the fallback.
        Select(registry, preferred: null, usable: p => p != "gemini")!.Name.Should().Be("groq_oss");
    }

    /// <summary>
    /// Every provider down and nothing excluded: use the first candidate anyway. A provider that may
    /// have recovered is a better bet than refusing to run the prompt.
    /// </summary>
    [Fact]
    public void All_down_fails_open_to_the_first_candidate()
    {
        Registry registry = new(Model("gemini_flash", "gemini", ModelTier.B), Model("gpt_mini", "openai", ModelTier.B));

        Select(registry, preferred: null, usable: _ => false)!.Name.Should().Be("gemini_flash");
    }

    /// <summary>An excluded provider (the one that just failed this call) is never chosen, even failing open.</summary>
    [Fact]
    public void An_excluded_provider_is_never_chosen()
    {
        Registry registry = new(Model("gemini_flash", "gemini", ModelTier.B), Model("gpt_mini", "openai", ModelTier.B));

        Select(registry, preferred: null, usable: null, excluded: ["gemini"])!.Name.Should().Be("gpt_mini");
        Select(new Registry(Model("gemini_flash", "gemini", ModelTier.B)), preferred: null, usable: _ => false, excluded: ["gemini"])
            .Should().BeNull("the only candidate is on the provider that just failed");
    }

    /// <summary>
    /// Availability is asked one candidate at a time and only until one is accepted, so a tracker
    /// that grants a single recovery trial hands it to the caller that actually makes the call.
    /// </summary>
    [Fact]
    public void Availability_is_asked_only_until_a_candidate_is_accepted()
    {
        Registry registry = new(Model("gemini_flash", "gemini", ModelTier.B), Model("gpt_mini", "openai", ModelTier.B));
        List<string> asked = [];

        Select(registry, preferred: null, usable: p => { asked.Add(p); return true; });

        asked.Should().Equal("gemini");
        registry.FindCalls.Should().Be(1, "the second tier candidate is never computed when the first is accepted");
    }

    /// <summary>Only failures that reflect the provider's health move a call to another provider.</summary>
    [Theory]
    [InlineData(402, """{"error":{"code":402,"message":"Your prepayment credits are depleted.","status":"RESOURCE_EXHAUSTED"}}""", true)]
    [InlineData(401, "", true)]
    [InlineData(503, "", true)]
    [InlineData(400, """{"error":{"message":"bad schema","type":"invalid_request_error","code":"json_validate_failed"}}""", false)]
    [InlineData(404, "", false)]
    public void Failover_happens_only_for_provider_health_failures(int status, string body, bool expected)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify((HttpStatusCode)status, body);
        InferenceProviderException exception = new("gemini", failure, "failed");

        InferService.ShouldFailOver(exception).Should().Be(expected);
        InferService.ShouldFailOver(null).Should().BeFalse();
    }

    /// <summary>A provider failure is found even when the client wraps it.</summary>
    [Fact]
    public void A_wrapped_provider_failure_is_found()
    {
        InferenceProviderException inner = new(
            "gemini", InferenceErrorClassifier.Classify(HttpStatusCode.PaymentRequired, ""), "failed");

        InferService.FindProviderFailure(new AggregateException(new InvalidOperationException("outer", inner)))
            .Should().BeSameAs(inner);
        InferService.FindProviderFailure(new InvalidOperationException("not a provider failure")).Should().BeNull();
    }
}
