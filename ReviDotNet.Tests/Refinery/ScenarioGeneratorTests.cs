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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>Unit tests for <see cref="ScenarioGenerator"/> against a fake <see cref="IInferService"/>.</summary>
public class ScenarioGeneratorTests
{
    private const string SpecSection =
        "The fact-checker determines which side of a disputed claim is correct, grounded in evidence.";

    /// <summary>
    /// The canned model output: two near-duplicate scenarios (same tags + same input values, only the id and
    /// notes differ) and one genuinely distinct scenario. The generator must collapse the duplicates and keep
    /// exactly one of them plus the distinct one.
    /// </summary>
    private static object CannedThreeItems() => new
    {
        scenarios = new object[]
        {
            new
            {
                id = "dup-a",
                inputs = new { claim = "The treaty was signed in 1648.", evidence = "Source A: 1648." },
                rubric = new[] { "Calibration" },
                expected_invariants = new[] { "FC-1" },
                held_out = false,
                tags = new[] { "factual", "dates" },
                notes = "first phrasing",
                ground_truth = "1648 is correct."
            },
            new
            {
                // Near-duplicate of dup-a: identical tag set + identical input VALUES (whitespace/order/casing
                // differences only); the fingerprint must collapse this into the same bucket as dup-a.
                id = "dup-b",
                inputs = new { the_claim = "the treaty was   signed in 1648.", src = "  Source A: 1648.  " },
                rubric = new[] { "Calibration" },
                expected_invariants = new[] { "FC-1" },
                held_out = false,
                tags = new[] { "Dates", "FACTUAL" },
                notes = "second phrasing of the same case",
                ground_truth = "1648 is correct."
            },
            new
            {
                id = "distinct",
                inputs = new { claim = "Company X grew 40% last quarter.", evidence = "No figures provided." },
                world_seed = "seed-7",
                rubric = new[] { "Refusal to fabricate" },
                expected_invariants = new[] { "FC-1", "FC-2" },
                held_out = true,
                tags = new[] { "insufficient-evidence" },
                notes = "no numbers in context",
                ground_truth = "Undetermined — no figures supplied."
            }
        }
    };

    [Fact]
    public async Task GenerateAsync_DedupsNearDuplicates_AndMapsFields()
    {
        FakeInferService infer = new(CannedThreeItems());
        ScenarioGenerator gen = new(infer);

        IReadOnlyList<Scenario> result =
            await gen.GenerateAsync("fact-checker", SpecSection, [], "factual contradiction", count: 5);

        // dup-a and dup-b collapse to one; distinct survives => 2 total.
        result.Should().HaveCount(2);

        // First kept is dup-a (the earlier of the two duplicates).
        Scenario first = result[0];
        first.Id.Should().Be("dup-a");
        first.AgentName.Should().Be("fact-checker");
        first.Inputs.Should().ContainKey("claim");
        first.Rubric.Should().ContainSingle().Which.Should().Be("Calibration");
        first.ExpectedInvariants.Should().ContainSingle().Which.Should().Be("FC-1");
        first.HeldOut.Should().BeFalse();
        first.Tags.Should().BeEquivalentTo(["factual", "dates"]);
        first.GroundTruth.Should().Be("1648 is correct.");
        first.WorldSeed.Should().BeNull();

        // The distinct scenario maps all of its fields.
        Scenario distinct = result[1];
        distinct.Id.Should().Be("distinct");
        distinct.WorldSeed.Should().Be("seed-7");
        distinct.HeldOut.Should().BeTrue();
        distinct.ExpectedInvariants.Should().BeEquivalentTo(["FC-1", "FC-2"]);
        distinct.Tags.Should().ContainSingle().Which.Should().Be("insufficient-evidence");
        distinct.GroundTruth.Should().Be("Undetermined — no figures supplied.");
        distinct.Notes.Should().Be("no numbers in context");

        // dup-b must NOT appear — it was the near-duplicate.
        result.Should().NotContain(s => s.Id == "dup-b");
    }

    [Fact]
    public async Task GenerateAsync_DedupsAgainstExistingSuite()
    {
        FakeInferService infer = new(CannedThreeItems());
        ScenarioGenerator gen = new(infer);

        // Pre-seed the existing suite with a scenario whose fingerprint matches dup-a/dup-b.
        Scenario existing = new()
        {
            Id = "pre-existing",
            AgentName = "fact-checker",
            Inputs = new Dictionary<string, string>
            {
                ["claim"] = "The treaty was signed in 1648.",
                ["evidence"] = "Source A: 1648."
            },
            Tags = ["factual", "dates"]
        };

        IReadOnlyList<Scenario> result =
            await gen.GenerateAsync("fact-checker", SpecSection, [existing], "factual contradiction", count: 5);

        // Both duplicates are dropped (matched the existing one); only the distinct scenario remains.
        result.Should().ContainSingle().Which.Id.Should().Be("distinct");
    }

    [Fact]
    public async Task GenerateAsync_RespectsCount()
    {
        FakeInferService infer = new(CannedThreeItems());
        ScenarioGenerator gen = new(infer);

        IReadOnlyList<Scenario> result =
            await gen.GenerateAsync("fact-checker", SpecSection, [], "factual contradiction", count: 1);

        result.Should().ContainSingle().Which.Id.Should().Be("dup-a");
    }

    [Fact]
    public async Task GenerateAsync_ReturnsEmpty_WhenInferReturnsNull()
    {
        FakeInferService infer = new(canned: null);
        ScenarioGenerator gen = new(infer);

        IReadOnlyList<Scenario> result =
            await gen.GenerateAsync("fact-checker", SpecSection, [], "factual contradiction");

        result.Should().BeEmpty();
    }

    /// <summary>
    /// A canned <see cref="IInferService"/> that deserializes a fixed JSON payload into whatever type the
    /// generator asks for via <c>ToObject&lt;T&gt;</c>. Newtonsoft binds by member, not accessibility, so it
    /// populates the generator's private response DTO.
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
            promptName.Should().Be("Evaluator.ScenarioGenerator");
            inputs.Should().NotBeNull();
            inputs!.Should().Contain(i => i.Label == "Target Category");
            inputs.Should().Contain(i => i.Label == "Count");
            T? value = _json is null ? default : JsonConvert.DeserializeObject<T>(_json);
            return Task.FromResult(value);
        }

        // ── Unused members — the generator only calls the multi-input ToObject<T> overload above. ──

        public Task<T?> ToObject<T>(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<(T? Value, CompletionResult? Usage)> ToObjectWithUsage<T>(string promptName, List<Input>? inputs, ModelProfile? model = null, string? modelName = null, CancellationToken ct = default) => throw new NotImplementedException();
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
