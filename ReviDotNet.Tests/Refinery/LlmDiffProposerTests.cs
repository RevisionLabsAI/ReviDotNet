// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>Unit tests for <see cref="LlmDiffProposer"/> against a fake <see cref="IInferService"/>.</summary>
public class LlmDiffProposerTests
{
    private const string CurrentDefinition =
        "[[information]]\nname = filer\n\n[[_system]]\nYou route reports. You may update a contradicting claim.\n";

    private const string RevisedDefinition =
        "[[information]]\nname = filer\n\n[[_system]]\nYou route reports. You MUST fact-check before updating a contradicting claim.\n";

    private static SuiteAggregate Scores() => new()
    {
        InvariantPassRate = 0.5,
        QualityMean = 5.0,
        QualityP10 = 2.0,
        RunCount = 4,
        GatedRunCount = 4,
        InvariantPassRateById = new Dictionary<string, double> { ["F-2"] = 0.0 }
    };

    private static IReadOnlyList<ScoreCard> Cards() =>
    [
        new ScoreCard
        {
            ScenarioId = "s1",
            AgentName = "filer",
            Invariants =
            [
                new InvariantResult { Id = "F-2", Passed = false, Severity = InvariantSeverity.Critical, Evidence = "update fired before fact-check" }
            ],
            Quality = new QualityScore
            {
                Overall = 5,
                Rationale = "Overrode a contradiction without fact-checking.",
                Facets = [new FacetScore("Conflict handling", 2, "Bypassed the fact-checker.")]
            }
        }
    ];

    [Fact]
    public async Task ProposeAsync_ReturnsProposal_WithKnobAndRevisedContent()
    {
        FakeInferService infer = new(new
        {
            knob_type = "guardrail",
            rationale = "Promote fact-check to a blocking precondition.",
            revised_definition = RevisedDefinition,
            expected_impact = "high — fixes F-2"
        });
        LlmDiffProposer proposer = new(infer, new MetaLlmUsageBroker());

        Proposal? result = await proposer.ProposeAsync("filer", CurrentDefinition, Scores(), Cards());

        result.Should().NotBeNull();
        result!.KnobType.Should().Be("guardrail");
        result.RevisedContent.Should().Be(RevisedDefinition);
        result.Rationale.Should().Be("Promote fact-check to a blocking precondition.");
        result.Diff.Should().NotBeNullOrWhiteSpace();
        result.Diff.Should().Contain("MUST fact-check"); // an added line shows up in the diff
    }

    [Fact]
    public async Task ProposeAsync_ReturnsNull_WhenRevisedEqualsCurrent()
    {
        FakeInferService infer = new(new
        {
            knob_type = "system-prompt",
            rationale = "no change",
            revised_definition = CurrentDefinition,
            expected_impact = "none"
        });
        LlmDiffProposer proposer = new(infer, new MetaLlmUsageBroker());

        Proposal? result = await proposer.ProposeAsync("filer", CurrentDefinition, Scores(), Cards());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ProposeAsync_ReturnsNull_WhenRevisedBlank()
    {
        FakeInferService infer = new(new
        {
            knob_type = "system-prompt",
            rationale = "no useful change",
            revised_definition = "   ",
            expected_impact = "none"
        });
        LlmDiffProposer proposer = new(infer, new MetaLlmUsageBroker());

        Proposal? result = await proposer.ProposeAsync("filer", CurrentDefinition, Scores(), Cards());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ProposeAsync_ReturnsNull_WhenInferReturnsNull()
    {
        FakeInferService infer = new(canned: null);
        LlmDiffProposer proposer = new(infer, new MetaLlmUsageBroker());

        Proposal? result = await proposer.ProposeAsync("filer", CurrentDefinition, Scores(), Cards());

        result.Should().BeNull();
    }

    /// <summary>
    /// A canned <see cref="IInferService"/> that deserializes a fixed JSON payload into whatever type the
    /// proposer asks for via <see cref="ToObject{T}(string,List{Input}?,ModelProfile?,string?,int,int?,CancellationToken)"/>.
    /// Deserializing into the proposer's private response DTO works because Newtonsoft binds by member, not accessibility.
    /// </summary>
    private sealed class FakeInferService(object? canned) : IInferService
    {
        private readonly string? _json = canned is null ? null : JsonConvert.SerializeObject(canned);

        public Task<T?> ToObject<T>(
            string promptName,
            List<Input>? inputs = null,
            ModelProfile? modelProfile = null,
            string? modelName = null,
            int retryAttempt = 0,
            int? originalRetryLimit = null,
            CancellationToken token = default)
        {
            promptName.Should().Be("Evaluator.Proposer");
            inputs.Should().NotBeNull();
            T? value = _json is null ? default : JsonConvert.DeserializeObject<T>(_json);
            return Task.FromResult(value);
        }

        // The proposer now calls ToObjectWithUsage; route it through the same canned-deserialize logic and
        // report no meta-token usage.
        public async Task<(T? Value, CompletionResult? Usage)> ToObjectWithUsage<T>(
            string promptName, List<Input>? inputs, ModelProfile? model = null, string? modelName = null, CancellationToken ct = default)
        {
            T? value = await ToObject<T>(promptName, inputs, model, modelName, token: ct);
            return (value, null);
        }

        // ── Unused members — the proposer only calls the multi-input ToObject<T> overload above. ──

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
