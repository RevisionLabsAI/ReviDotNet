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
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// Unit tests for <see cref="MetaAnalyzer.AnalyzeAsync"/>: grouping by (agent, knob), acceptance-rate math,
/// accepted-mean-quality-P10, and the case-insensitive agent filter. The store is a hand-rolled fake that
/// returns canned campaigns and ledger entries — no DI, no real persistence.
/// </summary>
public class MetaAnalyzerTests
{
    private static SuiteAggregate Scores(double p10) => new() { QualityP10 = p10 };

    private static LedgerEntry Entry(
        string campaignId, string agent, string knob, bool accepted,
        double? trainP10 = null, double? heldOutP10 = null) => new()
    {
        CampaignId = campaignId,
        Round = 1,
        AgentName = agent,
        KnobType = knob,
        Accepted = accepted,
        TrainScores = trainP10 is { } t ? Scores(t) : null,
        HeldOutScores = heldOutP10 is { } h ? Scores(h) : null
    };

    [Fact]
    public async Task AnalyzeAsync_GroupsByAgentAndKnob_AggregatesAcrossCampaigns()
    {
        FakeStore store = new();
        store.AddCampaign("c1",
            Entry("c1", "chatbot", "system-prompt", accepted: true, trainP10: 8),
            Entry("c1", "chatbot", "system-prompt", accepted: false),
            Entry("c1", "chatbot", "sampling", accepted: false));
        store.AddCampaign("c2",
            Entry("c2", "chatbot", "system-prompt", accepted: true, trainP10: 6));

        IReadOnlyList<KnobEffectiveness> result = await new MetaAnalyzer(store).AnalyzeAsync();

        // Two distinct (agent, knob) groups: chatbot/system-prompt across both campaigns + chatbot/sampling.
        result.Should().HaveCount(2);

        KnobEffectiveness sysPrompt = result.Single(k => k.KnobType == "system-prompt");
        sysPrompt.AgentName.Should().Be("chatbot");
        sysPrompt.Attempts.Should().Be(3);   // 2 in c1 + 1 in c2
        sysPrompt.Accepted.Should().Be(2);
        sysPrompt.AcceptanceRate.Should().BeApproximately(2.0 / 3.0, 1e-9);
        sysPrompt.AcceptedMeanQualityP10.Should().BeApproximately(7.0, 1e-9); // (8 + 6) / 2

        KnobEffectiveness sampling = result.Single(k => k.KnobType == "sampling");
        sampling.Attempts.Should().Be(1);
        sampling.Accepted.Should().Be(0);
        sampling.AcceptanceRate.Should().Be(0);
        sampling.AcceptedMeanQualityP10.Should().Be(0); // no accepted entries
    }

    [Fact]
    public async Task AnalyzeAsync_FallsBackToHeldOutP10_WhenTrainScoresMissing()
    {
        FakeStore store = new();
        store.AddCampaign("c1",
            Entry("c1", "chatbot", "few-shot", accepted: true, trainP10: null, heldOutP10: 9));

        IReadOnlyList<KnobEffectiveness> result = await new MetaAnalyzer(store).AnalyzeAsync();

        result.Single().AcceptedMeanQualityP10.Should().Be(9);
    }

    [Fact]
    public async Task AnalyzeAsync_OrdersByAcceptanceRateDesc_ThenAttemptsDesc()
    {
        FakeStore store = new();
        store.AddCampaign("c1",
            // "good" knob: 1/1 accepted (rate 1.0)
            Entry("c1", "a", "good", accepted: true, trainP10: 5),
            // "ok" knob: 2/4 accepted (rate 0.5, 4 attempts)
            Entry("c1", "a", "ok", accepted: true, trainP10: 5),
            Entry("c1", "a", "ok", accepted: true, trainP10: 5),
            Entry("c1", "a", "ok", accepted: false),
            Entry("c1", "a", "ok", accepted: false),
            // "mid" knob: 1/2 accepted (rate 0.5, 2 attempts) — same rate as "ok" but fewer attempts
            Entry("c1", "a", "mid", accepted: true, trainP10: 5),
            Entry("c1", "a", "mid", accepted: false));

        IReadOnlyList<KnobEffectiveness> result = await new MetaAnalyzer(store).AnalyzeAsync();

        result.Select(k => k.KnobType).Should().ContainInOrder("good", "ok", "mid");
    }

    [Fact]
    public async Task AnalyzeAsync_FiltersByAgentName_CaseInsensitive()
    {
        FakeStore store = new();
        store.AddCampaign("c1",
            Entry("c1", "Chatbot", "system-prompt", accepted: true, trainP10: 7),
            Entry("c1", "Summarizer", "system-prompt", accepted: true, trainP10: 4));

        IReadOnlyList<KnobEffectiveness> result = await new MetaAnalyzer(store).AnalyzeAsync("chatbot");

        result.Should().ContainSingle();
        result.Single().AgentName.Should().Be("Chatbot");
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyStore_ReturnsEmpty()
    {
        IReadOnlyList<KnobEffectiveness> result = await new MetaAnalyzer(new FakeStore()).AnalyzeAsync();
        result.Should().BeEmpty();
    }

    /// <summary>A canned <see cref="ICampaignStore"/>: campaigns + their ledgers, populated in-test.</summary>
    private sealed class FakeStore : ICampaignStore
    {
        private readonly List<Campaign> _campaigns = [];
        private readonly Dictionary<string, List<LedgerEntry>> _ledger = [];

        public void AddCampaign(string id, params LedgerEntry[] entries)
        {
            _campaigns.Add(new Campaign { Id = id, Spec = SpecFor(id) });
            _ledger[id] = entries.ToList();
        }

        private static CampaignSpec SpecFor(string id) => new()
        {
            PluginName = "gd",
            AgentName = "chatbot",
            SuiteName = $"suite-{id}"
        };

        public Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Campaign>>(_campaigns);

        public Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(string campaignId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LedgerEntry>>(_ledger.GetValueOrDefault(campaignId) ?? []);

        public Task SaveAsync(Campaign campaign, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Campaign?> GetAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AppendLedgerAsync(LedgerEntry entry, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
