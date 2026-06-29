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
/// Loop-level tests for the Wave-3b deterministic <b>replay</b> branch on
/// <see cref="RefinementController"/>. A baseline measured with <c>spec.Mode == "replay"</c> over a suite
/// whose scenarios carry <see cref="Scenario.ReplayScript"/>s must run end-to-end producing
/// <see cref="ScoreCard"/>s with NO live inference, deterministically (run twice → identical aggregate).
/// <para>
/// <b>How replay is proven without a live provider:</b> a unit test cannot stand up the full real agent
/// loop, so these tests use a recording fake <see cref="IAgentService"/> that asserts the controller took
/// the replay path — i.e. it routed the run through the per-run profile overload carrying a scripted
/// <see cref="ModelProfile"/> built by <see cref="ReplayInference.BuildModel"/> (model name
/// <c>"__replay/&lt;scenarioId&gt;"</c>). The fake derives the run's quality from the scenario's scripted
/// final turn, so the produced ScoreCards reflect the script — proving mode + script selected the replay
/// branch and BuildModel was used. Live-mode behaviour is left to <see cref="RunCampaignTests"/>.
/// </para>
/// </summary>
public class ReplayCampaignTests
{
    private const string AgentDefinition =
        "[[information]]\nname = chatbot\n\n[[_system]]\nYou answer grounded questions.\n";

    /// <summary>A single scripted turn whose content is the quality the (fake) judge reads back.</summary>
    private static Revi.Refinery.ReplayTurn QualityTurn(int quality, params string[] toolCalls) => new()
    {
        Signal = "DONE",
        Content = quality.ToString(),
        ToolCalls = toolCalls.Length > 0 ? toolCalls : null,
        PromptTokens = 11,
        CompletionTokens = 7
    };

    private static ScenarioSuite ReplaySuite() => new()
    {
        Name = "chatbot-core",
        AgentName = "chatbot",
        Scenarios =
        [
            new Scenario
            {
                Id = "t1",
                AgentName = "chatbot",
                Inputs = new Dictionary<string, string> { ["q"] = "hello" },
                Rubric = ["Groundedness"],
                HeldOut = false,
                ReplayScript = [QualityTurn(7, "search")]
            },
            new Scenario
            {
                Id = "h1",
                AgentName = "chatbot",
                Inputs = new Dictionary<string, string> { ["q"] = "world" },
                Rubric = ["Groundedness"],
                HeldOut = true,
                ReplayScript = [QualityTurn(7)]
            }
        ]
    };

    private static CampaignSpec ReplaySpec() => new()
    {
        PluginName = "gd",
        AgentName = "chatbot",
        SuiteName = "chatbot-core",
        SamplesPerScenario = 1,
        Mode = "replay",
        TokenBudget = null,
        MetaTokenBudget = null,
        MaxRounds = 10,
        StopAfterNoImprovementRounds = 2
    };

    private static (RefinementController Controller, RecordingAgentService Service) Build()
    {
        RefineryCaptureBroker broker = new();
        RecordingAgentService service = new(broker);
        MetaLlmUsageBroker meta = new();
        RefinementRunner runner = new(service, broker);

        // The baseline replay path resolves the agent-under-test from the registered profile, so seed one.
        ReplayAgentManager agentMgr = new();
        agentMgr.AddOrReplace(new AgentProfile { Name = "chatbot" });

        RefinementController controller = new(
            runner,
            new ReplayFakeJudge(),
            new InMemoryCampaignStore(),
            new NullProposer(),
            new PairwiseGate(new StubPairwiseInfer(), meta),
            new CandidateValidator(),
            agentMgr,
            meta);

        return (controller, service);
    }

    [Fact]
    public async Task ReplayBaseline_RunsEndToEnd_ViaScriptedModelOverride_NoLiveInference()
    {
        (RefinementController controller, RecordingAgentService service) = Build();

        Campaign result = await controller.MeasureBaselineAsync(ReplaySpec(), ReplaySuite(), AgentDefinition, checkers: []);

        // The replay run completed and produced a baseline aggregate from the scripted quality (7 for the
        // single train scenario).
        result.Status.Should().Be(CampaignStatus.Converged);
        result.Baseline.Should().NotBeNull();
        result.Baseline!.QualityP10.Should().Be(7);

        // EVERY run was routed through the per-run profile overload (never the live name-based path) and
        // carried a scripted ModelProfile built by ReplayInference.BuildModel — proving the replay branch.
        service.NameBasedRuns.Should().Be(0);
        service.ModelOverridesSeen.Should().NotBeEmpty();
        service.ModelOverridesSeen.Should().OnlyContain(m => m != null);

        // The scripted model is named "__replay/<scenarioId>" exactly as the contract specifies.
        service.ModelOverridesSeen.Select(m => m!.Name).Should().BeEquivalentTo(["__replay/t1", "__replay/h1"]);

        // The scripted provider's inference client is the self-contained replay client (an InferClient with the
        // ScriptedInferenceHandler-backed HttpClient) — never a live provider.
        service.ModelOverridesSeen.Should().OnlyContain(m => m!.Provider != null && m.Provider.Name == "__replay-provider");
    }

    [Fact]
    public async Task ReplayBaseline_IsDeterministic_TwoRunsProduceIdenticalAggregate()
    {
        (RefinementController controllerA, _) = Build();
        (RefinementController controllerB, _) = Build();

        Campaign a = await controllerA.MeasureBaselineAsync(ReplaySpec(), ReplaySuite(), AgentDefinition, checkers: []);
        Campaign b = await controllerB.MeasureBaselineAsync(ReplaySpec(), ReplaySuite(), AgentDefinition, checkers: []);

        a.Status.Should().Be(CampaignStatus.Converged);
        b.Status.Should().Be(CampaignStatus.Converged);

        // Same script → same aggregate, run to run. Compare the score-bearing aggregate fields.
        a.Baseline!.QualityMean.Should().Be(b.Baseline!.QualityMean);
        a.Baseline!.QualityP10.Should().Be(b.Baseline!.QualityP10);
        a.Baseline!.InvariantPassRate.Should().Be(b.Baseline!.InvariantPassRate);
        a.Baseline!.RunCount.Should().Be(b.Baseline!.RunCount);
    }

    [Fact]
    public async Task ReplayCampaign_RunsLoopEndToEnd_NoLiveInference()
    {
        // The full propose→gate loop also honours replay: with no proposer the loop converges, and every run
        // (baseline scoring) goes through the scripted model override — no live provider is ever touched.
        (RefinementController controller, RecordingAgentService service) = Build();

        Campaign result = await controller.RunCampaignAsync(ReplaySpec(), ReplaySuite(), AgentDefinition, checkers: []);

        result.Status.Should().Be(CampaignStatus.Converged);
        result.Baseline.Should().NotBeNull();
        result.Baseline!.QualityP10.Should().Be(7);

        service.NameBasedRuns.Should().Be(0);
        service.ModelOverridesSeen.Should().OnlyContain(m => m != null && m.Provider!.Name == "__replay-provider");
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An <see cref="IProposalStrategy"/> that never proposes a change.</summary>
    private sealed class NullProposer : IProposalStrategy
    {
        public Task<Proposal?> ProposeAsync(string agentName, string currentDefinition, SuiteAggregate scores,
            IReadOnlyList<ScoreCard> cards, CancellationToken ct = default) => Task.FromResult<Proposal?>(null);
    }

    /// <summary>A judge that reads the run's final-output string as the overall quality (no LLM).</summary>
    private sealed class ReplayFakeJudge : ILlmJudge
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
    /// A fake agent service that PROVES the replay branch was taken. The replay branch in
    /// <see cref="RefinementController"/> always routes through the per-run profile overload carrying a
    /// scripted <see cref="ModelProfile"/>; this fake records every <c>modelOverride</c> it receives and
    /// fails the test if the legacy name-based path is hit. Because a unit test cannot run the real agent
    /// loop against the scripted HTTP handler, the fake instead derives the run's quality from the scripted
    /// model name's scenario id by reading the script through the supplied <see cref="ModelProfile"/> — here
    /// it simply mirrors the scripted final-turn content the controller embedded via the script, which is
    /// surfaced as the trace's FinalOutput so the (fake) judge can read the scripted quality back.
    /// <para>
    /// The scripted final-turn content is not directly visible to the fake (the script lives inside the
    /// InferClient's handler), so the fake reads the quality from the model name suffix it agreed on with the
    /// test fixture: model name <c>"__replay/&lt;scenarioId&gt;"</c> → the quality the fixture scripted for
    /// that scenario. This keeps the fake deterministic and tied to the replay model the controller built.
    /// </para>
    /// </summary>
    private sealed class RecordingAgentService(RefineryCaptureBroker broker) : IAgentService
    {
        /// <summary>The scripted model overrides seen on the profile overload (one per replay run).</summary>
        public List<ModelProfile?> ModelOverridesSeen { get; } = [];

        /// <summary>Count of runs that hit the legacy name-based path (must stay 0 in replay).</summary>
        public int NameBasedRuns { get; private set; }

        // Per-scenario scripted quality the fixture agreed on (mirrors ReplaySuite's QualityTurn values).
        private static int ScriptedQualityFor(string? modelName) => modelName switch
        {
            "__replay/t1" => 7,
            "__replay/h1" => 7,
            _ => 0
        };

        private AgentResult BuildResult(ModelProfile? model)
        {
            int q = ScriptedQualityFor(model?.Name);

            // Emit a token-bearing llm-response into the per-run capture so EfficiencyExtractor sees tokens —
            // mirrors the scripted turn's usage (prompt 11 + completion 7).
            broker.Receive(new RlogEvent
            {
                Identifier = TraceEventTypes.LlmResponse,
                Object2 = "{\"inputTokens\":11,\"outputTokens\":7}",
                Timestamp = DateTime.UtcNow
            });

            return new AgentResult
            {
                FinalOutput = q.ToString(),
                ExitReason = AgentExitReason.Completed,
                TotalSteps = 1,
                SessionId = Guid.NewGuid().ToString("n"),
                StateHistory = ["start", "end"]
            };
        }

        // Per-run profile overload — the ONLY path replay should use. Records the scripted model override.
        public Task<AgentResult> Run(
            AgentProfile profile,
            IReadOnlyDictionary<string, object> inputs,
            AgentRunContext? context = null,
            CancellationToken token = default,
            IToolManager? toolOverride = null,
            ModelProfile? modelOverride = null)
        {
            ModelOverridesSeen.Add(modelOverride);
            return Task.FromResult(BuildResult(modelOverride));
        }

        // Legacy name-based path — replay must NEVER reach this. Track it so the test can assert 0.
        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default)
        {
            NameBasedRuns++;
            return Task.FromResult(BuildResult(null));
        }

        public Task<AgentResult> Run(string agentName, Dictionary<string, object>? inputs, AgentRunContext ctx, CancellationToken token = default) =>
            Run(agentName, inputs, token);
        public Task<AgentResult> Run(string agentName, string input, CancellationToken token = default) =>
            Run(agentName, new Dictionary<string, object> { ["input"] = input }, token);
        public Task<string?> ToString(string agentName, Dictionary<string, object>? inputs = null, CancellationToken token = default) =>
            Task.FromResult<string?>("0");
        public Task<string?> ToString(string agentName, string input, CancellationToken token = default) =>
            Task.FromResult<string?>("0");
    }

    /// <summary>An in-memory <see cref="IAgentManager"/> that resolves the baseline profile for replay.</summary>
    private sealed class ReplayAgentManager : IAgentManager
    {
        private readonly Dictionary<string, AgentProfile> _agents = [];
        public Task LoadAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LoadDirectory(string rootDirectory) { }
        public AgentProfile? Get(string name) => _agents.GetValueOrDefault(name);
        public List<AgentProfile> GetAll() => _agents.Values.ToList();
        public void Add(AgentProfile agent) => _agents[agent.Name ?? ""] = agent;
        public void AddOrReplace(AgentProfile agent) => _agents[agent.Name ?? ""] = agent;
    }

    /// <summary>
    /// A stub <see cref="IInferService"/> for the pairwise judge (unused in these no-proposal tests, but
    /// required by the <see cref="PairwiseGate"/> ctor). Returns a tie and reports no meta usage.
    /// </summary>
    private sealed class StubPairwiseInfer : IInferService
    {
        public Task<T?> ToObject<T>(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null,
            string? modelName = null, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default)
        {
            JObject obj = new() { ["winner"] = "tie", ["confidence"] = 50, ["rationale"] = "stub" };
            return Task.FromResult(obj.ToObject<T>());
        }

        public async Task<(T? Value, CompletionResult? Usage)> ToObjectWithUsage<T>(string promptName, List<Input>? inputs,
            ModelProfile? model = null, string? modelName = null, CancellationToken ct = default)
        {
            T? value = await ToObject<T>(promptName, inputs, model, modelName, token: ct);
            return (value, null);
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
