// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// The file-backed campaign store must round-trip the SDK records across store instances (i.e. across
/// process restarts) — that durability is its whole reason for existing after an in-memory campaign was
/// lost to a Forge process death mid-run.
/// </summary>
public sealed class FileCampaignStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "revi-filestore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Campaign MakeCampaign(string id) => new()
    {
        Id = id,
        Spec = new CampaignSpec
        {
            PluginName = "TestPlugin",
            AgentName = "tester",
            SuiteName = "suite-a",
            SamplesPerScenario = 2,
            Mode = "replay",
            TokenBudget = 1000,
            MetaTokenBudget = 2000
        },
        Status = CampaignStatus.Running,
        Baseline = new SuiteAggregate { InvariantPassRate = 0.5, QualityMean = 7.5, QualityP10 = 6, RunCount = 4 },
        TokensSpent = 123,
        MetaTokensSpent = 456
    };

    [Fact]
    public async Task Campaigns_round_trip_across_store_instances()
    {
        var store = new FileCampaignStore(_root);
        await store.SaveAsync(MakeCampaign("camp-1"));
        await store.SaveAsync(MakeCampaign("camp-1") with { Status = CampaignStatus.Converged, TokensSpent = 999 });
        await store.SaveAsync(MakeCampaign("camp-2"));

        // A NEW instance over the same directory simulates a process restart.
        var reopened = new FileCampaignStore(_root);

        Campaign? c1 = await reopened.GetAsync("camp-1");
        c1.Should().NotBeNull();
        c1!.Status.Should().Be(CampaignStatus.Converged, "the second save must overwrite the first");
        c1.TokensSpent.Should().Be(999);
        c1.Spec.PluginName.Should().Be("TestPlugin");
        c1.Baseline!.QualityMean.Should().Be(7.5);

        (await reopened.ListAsync()).Should().HaveCount(2);
        (await reopened.GetAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task Ledger_appends_survive_restart_and_read_back_in_round_order()
    {
        var store = new FileCampaignStore(_root);
        await store.AppendLedgerAsync(new LedgerEntry { CampaignId = "camp-1", Round = 2, AgentName = "tester", Accepted = false });
        await store.AppendLedgerAsync(new LedgerEntry { CampaignId = "camp-1", Round = 1, AgentName = "tester", Accepted = true, Diff = "d1" });
        await store.AppendLedgerAsync(new LedgerEntry { CampaignId = "other", Round = 1, AgentName = "tester" });

        var reopened = new FileCampaignStore(_root);
        IReadOnlyList<LedgerEntry> ledger = await reopened.GetLedgerAsync("camp-1");

        ledger.Should().HaveCount(2, "entries are per-campaign");
        ledger[0].Round.Should().Be(1);
        ledger[0].Accepted.Should().BeTrue();
        ledger[0].Diff.Should().Be("d1");
        ledger[1].Round.Should().Be(2);
        (await reopened.GetLedgerAsync("unknown")).Should().BeEmpty();
    }

    [Fact]
    public async Task Score_cards_and_ground_truth_survive_restart()
    {
        var store = new FileCampaignStore(_root);
        await store.SaveScoreCardsAsync("camp-1",
        [
            new ScoreCard { ScenarioId = "s1", AgentName = "tester", Quality = new QualityScore { Overall = 8 } },
            new ScoreCard { ScenarioId = "s2", AgentName = "tester" }
        ]);
        await store.SaveGroundTruthAsync(new Dictionary<string, string> { ["s1"] = "truth-1" });
        await store.SaveGroundTruthAsync(new Dictionary<string, string> { ["s2"] = "truth-2" });

        var reopened = new FileCampaignStore(_root);

        IReadOnlyList<ScoreCard> cards = await reopened.GetScoreCardsAsync();
        cards.Should().HaveCount(2);
        cards.Single(c => c.ScenarioId == "s1").Quality!.Overall.Should().Be(8);

        IReadOnlyDictionary<string, string> truth = await reopened.GetGroundTruthAsync();
        truth.Should().HaveCount(2, "successive saves merge rather than replace");
        truth["s1"].Should().Be("truth-1");
        truth["s2"].Should().Be("truth-2");
    }

    [Fact]
    public async Task Corrupt_files_are_skipped_not_fatal()
    {
        var store = new FileCampaignStore(_root);
        await store.SaveAsync(MakeCampaign("camp-1"));
        await store.AppendLedgerAsync(new LedgerEntry { CampaignId = "camp-1", Round = 1, AgentName = "tester" });

        // Corrupt one campaign file and inject a garbage ledger line.
        await File.WriteAllTextAsync(Path.Combine(_root, "campaigns", "camp-broken.json"), "{not json");
        await File.AppendAllTextAsync(Path.Combine(_root, "ledger", "camp-1.jsonl"), "garbage line\n");

        var reopened = new FileCampaignStore(_root);
        (await reopened.ListAsync()).Should().ContainSingle(c => c.Id == "camp-1");
        (await reopened.GetLedgerAsync("camp-1")).Should().HaveCount(1);
    }
}
