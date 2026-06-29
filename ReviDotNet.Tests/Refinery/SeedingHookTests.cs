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
using Newtonsoft.Json.Linq;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// Tests for the Phase 5 seeding hook: the engine must invoke the <c>seedScenario</c> callback exactly once
/// per (scenario × sample) and BEFORE each agent run. Mirrors the fake run-pipeline setup from
/// <see cref="RunCampaignTests"/> (fake <see cref="IAgentService"/> + real <see cref="RefineryCaptureBroker"/>,
/// fake judge, in-memory store, stub proposer/pairwise/validator/agent-manager).
/// </summary>
public class SeedingHookTests
{
    private const string AgentDefinition =
        "[[information]]\nname = chatbot\n\n[[_system]]\nYou answer grounded questions.\n";

    private static ScenarioSuite Suite() => new()
    {
        Name = "chatbot-core",
        AgentName = "chatbot",
        Scenarios =
        [
            new Scenario { Id = "t1", AgentName = "chatbot", Inputs = new Dictionary<string, string> { ["q"] = "hello" }, Rubric = ["Groundedness"], HeldOut = false },
            new Scenario { Id = "t2", AgentName = "chatbot", Inputs = new Dictionary<string, string> { ["q"] = "world" }, Rubric = ["Groundedness"], HeldOut = false }
        ]
    };

    private static CampaignSpec Spec(int samples) => new()
    {
        PluginName = "gd",
        AgentName = "chatbot",
        SuiteName = "chatbot-core",
        SamplesPerScenario = samples,
        Mode = "live",
        TokenBudget = null,
        MaxRounds = 10,
        StopAfterNoImprovementRounds = 2
    };

    private static RefinementController Controller(IAgentService agentService, RefineryCaptureBroker broker) =>
        new(
            new RefinementRunner(agentService, broker),
            new FakeJudge(),
            new InMemoryCampaignStore(),
            new NullProposer(),
            new PairwiseGate(new StubPairwiseInfer()),
            new CandidateValidator(),
            new FakeAgentManager());

    [Fact]
    public async Task MeasureBaseline_InvokesSeedCallback_OncePerScenarioSampleBeforeEachRun()
    {
        const int samples = 3;
        RefineryCaptureBroker broker = new();
        var seedCalls = new List<(string ScenarioId, int RunCountAtSeed)>();
        var runs = new List<string>();

        FakeAgentService agentService = new(broker, quality: 5, onRun: name => runs.Add(name));
        RefinementController controller = Controller(agentService, broker);

        Func<Scenario, CancellationToken, Task> seed = (s, _) =>
        {
            // Record both WHICH scenario was seeded and HOW MANY runs had executed at seed time, so we can
            // assert the seed happened BEFORE its corresponding run.
            seedCalls.Add((s.Id, runs.Count));
            return Task.CompletedTask;
        };

        await controller.MeasureBaselineAsync(
            Spec(samples), Suite(), AgentDefinition, checkers: [], progress: null, ct: default,
            campaignId: null, seedScenario: seed);

        // Exactly one seed per (scenario × sample).
        int expected = Suite().Scenarios.Count * samples; // 2 × 3 = 6
        seedCalls.Should().HaveCount(expected);
        runs.Should().HaveCount(expected);

        // Each seed precedes its run: the run count captured AT seed time equals the index of that seed,
        // i.e. the Nth seed fires before the Nth run (runs.Count had not yet been incremented).
        for (int i = 0; i < seedCalls.Count; i++)
            seedCalls[i].RunCountAtSeed.Should().Be(i, "seed #{0} must fire before run #{0}", i);

        // Both scenarios were seeded exactly `samples` times each.
        seedCalls.Count(c => c.ScenarioId == "t1").Should().Be(samples);
        seedCalls.Count(c => c.ScenarioId == "t2").Should().Be(samples);
    }

    [Fact]
    public async Task MeasureBaseline_NullSeedCallback_RunsWithoutSeeding()
    {
        RefineryCaptureBroker broker = new();
        var runs = new List<string>();
        FakeAgentService agentService = new(broker, quality: 5, onRun: name => runs.Add(name));
        RefinementController controller = Controller(agentService, broker);

        // Backward-compatible: omitting the callback (null) must not throw and still runs every sample.
        Campaign result = await controller.MeasureBaselineAsync(Spec(samples: 2), Suite(), AgentDefinition, checkers: []);

        result.Status.Should().Be(CampaignStatus.Converged);
        runs.Should().HaveCount(Suite().Scenarios.Count * 2);
    }

    // ── Fakes (mirrors RunCampaignTests) ──────────────────────────────────────────────────────────────

    /// <summary>An <see cref="IProposalStrategy"/> that never proposes a change.</summary>
    private sealed class NullProposer : IProposalStrategy
    {
        public Task<Proposal?> ProposeAsync(string agentName, string currentDefinition, SuiteAggregate scores,
            IReadOnlyList<ScoreCard> cards, CancellationToken ct = default) => Task.FromResult<Proposal?>(null);
    }

    /// <summary>A judge that reads the run's final-output string as the overall quality (no LLM).</summary>
    private sealed class FakeJudge : ILlmJudge
    {
        public Task<QualityScore?> JudgeAsync(AgentTrace trace, string agentDefinition, Scenario scenario,
            IReadOnlyList<InvariantResult> invariants, CancellationToken ct = default)
        {
            int overall = int.TryParse(trace.FinalOutput, out int q) ? q : 0;
            return Task.FromResult<QualityScore?>(new QualityScore
            {
                Overall = overall,
                Rationale = "fake",
                Facets = [new FacetScore("Groundedness", overall, "fake")]
            });
        }
    }

    /// <summary>
    /// A fake agent service whose run output is the quality the judge reports. Invokes <paramref name="onRun"/>
    /// on each run so the test can observe run ordering relative to seed calls.
    /// </summary>
    private sealed class FakeAgentService(RefineryCaptureBroker broker, int quality, Action<string> onRun) : IAgentService
    {
        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default)
        {
            onRun(agentName);
            _ = broker; // capture scope is established by the runner; nothing extra to emit here.
            return Task.FromResult(new AgentResult
            {
                FinalOutput = quality.ToString(),
                ExitReason = AgentExitReason.Completed,
                TotalSteps = 1,
                SessionId = Guid.NewGuid().ToString("n"),
                StateHistory = ["start", "end"]
            });
        }

        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs, AgentRunContext ctx, CancellationToken token = default) =>
            Run(agentName, inputs, token);
        public Task<AgentResult> Run(string agentName, string input, CancellationToken token = default) =>
            Run(agentName, new Dictionary<string, object> { ["input"] = input }, token);
        public Task<string?> ToString(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default) =>
            Task.FromResult<string?>(quality.ToString());
        public Task<string?> ToString(string agentName, string input, CancellationToken token = default) =>
            Task.FromResult<string?>(quality.ToString());
    }

    /// <summary>An in-memory <see cref="IAgentManager"/> stub (only AddOrReplace is exercised by the loop).</summary>
    private sealed class FakeAgentManager : IAgentManager
    {
        private readonly Dictionary<string, AgentProfile> _agents = [];
        public Task LoadAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LoadDirectory(string rootDirectory) { }
        public AgentProfile? Get(string name) => _agents.GetValueOrDefault(name);
        public List<AgentProfile> GetAll() => _agents.Values.ToList();
        public void Add(AgentProfile agent) => _agents[agent.Name ?? ""] = agent;
        public void AddOrReplace(AgentProfile agent) => _agents[agent.Name ?? ""] = agent;
    }

    /// <summary>A stub <see cref="IInferService"/> for the pairwise judge (never reached: NullProposer ends the loop).</summary>
    private sealed class StubPairwiseInfer : IInferService
    {
        public Task<T?> ToObject<T>(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null,
            string? modelName = null, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default)
        {
            JObject obj = new() { ["winner"] = "tie", ["confidence"] = 50, ["rationale"] = "stub" };
            return Task.FromResult(obj.ToObject<T>());
        }

        public Task<T?> ToObject<T>(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CompletionResult?> Completion(Prompt prompt, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken token = default, bool directRoute = false) => throw new NotImplementedException();
        public Task<CompletionResult?> Completion(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken token = default) => throw new NotImplementedException();
        public IAsyncEnumerable<string> CompletionStream(Prompt prompt, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken cancellationToken = default, bool directRoute = false) => throw new NotImplementedException();
        public Task<TEnum> ToEnum<TEnum>(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, bool includeEnumValues = false, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default) where TEnum : struct, Enum => throw new NotImplementedException();
        public Task<TEnum> ToEnum<TEnum>(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, bool includeEnumValues = false, CancellationToken token = default) where TEnum : struct, Enum => throw new NotImplementedException();
        public Task<string?> ToString(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<string?> ToString(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<bool?> ToBool(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<bool?> ToBool(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<JObject?> ToJObject(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<JObject?> ToJObject(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringList(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringList(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListClean(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListClean(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListLimited(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, int? maxLines = null, Func<string, bool>? evaluator = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<List<string>> ToStringListLimited(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, int? maxLines = null, Func<string, bool>? evaluator = null, CancellationToken token = default) => throw new NotImplementedException();
        public Prompt FindPrompt(string name) => throw new NotImplementedException();
        public string? ListInputs(ModelProfile model, List<Input>? inputs) => throw new NotImplementedException();
    }
}
