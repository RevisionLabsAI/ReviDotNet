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
using Newtonsoft.Json.Linq;
using Revi;
using ReviDotNet.Forge.Services;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Deterministic unit tests for <see cref="SuiteAssertionEvaluator"/>. The pure kinds (Contains, NotContains,
/// Regex, JsonPath) never touch inference; ScoreMin is exercised with a hand-rolled fake
/// <see cref="IInferService"/> and with a null judge. No live inference or agents are run here.
/// </summary>
public class SuiteAssertionTests
{
    private static SuiteAssertion A(AssertionKind kind, string target, double? threshold = null)
        => new($"a-{kind}", kind, target, threshold);

    private static Task<IReadOnlyList<AssertionResult>> Eval(string output, SuiteAssertion a, IInferService? judge = null)
        => SuiteAssertionEvaluator.EvaluateAsync(output, new[] { a }, judge);

    // ── empty / null assertions ──────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_NullAssertions_ReturnsEmpty()
    {
        var results = await SuiteAssertionEvaluator.EvaluateAsync("anything", null!);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_EmptyAssertions_ReturnsEmpty()
    {
        var results = await SuiteAssertionEvaluator.EvaluateAsync("anything", Array.Empty<SuiteAssertion>());
        results.Should().BeEmpty();
    }

    // ── Contains ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Contains_Pass_CaseInsensitive()
    {
        var results = await Eval("The Quick Brown Fox", A(AssertionKind.Contains, "quick brown"));
        results.Should().ContainSingle();
        results[0].Passed.Should().BeTrue();
        results[0].FailReason.Should().BeNull();
    }

    [Fact]
    public async Task Contains_Fail_SetsSnippetAndReason()
    {
        var results = await Eval("hello world", A(AssertionKind.Contains, "goodbye"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("goodbye");
        results[0].ActualSnippet.Should().Be("hello world");
    }

    // ── NotContains ──────────────────────────────────────────────────────

    [Fact]
    public async Task NotContains_Pass_WhenAbsent()
    {
        var results = await Eval("hello world", A(AssertionKind.NotContains, "error"));
        results[0].Passed.Should().BeTrue();
    }

    [Fact]
    public async Task NotContains_Fail_WhenPresent()
    {
        var results = await Eval("fatal ERROR occurred", A(AssertionKind.NotContains, "error"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("error");
    }

    // ── Regex ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Regex_Pass_CapturesMatchSnippet()
    {
        var results = await Eval("order id: 12345 done", A(AssertionKind.Regex, @"\d{5}"));
        results[0].Passed.Should().BeTrue();
        results[0].ActualSnippet.Should().Be("12345");
    }

    [Fact]
    public async Task Regex_Fail_NoMatch()
    {
        var results = await Eval("no digits here", A(AssertionKind.Regex, @"\d{5}"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("does not match");
    }

    [Fact]
    public async Task Regex_Fail_InvalidPattern_GuardedWithReason()
    {
        var results = await Eval("anything", A(AssertionKind.Regex, "(unclosed"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("Invalid regex");
    }

    // ── JsonPath ─────────────────────────────────────────────────────────

    [Fact]
    public async Task JsonPath_Pass_DottedObjectPath()
    {
        string json = """{"a":{"b":{"c":"value"}}}""";
        var results = await Eval(json, A(AssertionKind.JsonPath, "a.b.c"));
        results[0].Passed.Should().BeTrue();
        results[0].ActualSnippet.Should().Be("value");
    }

    [Fact]
    public async Task JsonPath_Pass_ArrayIndexPath()
    {
        string json = """{"items":[{"name":"first"},{"name":"second"}]}""";
        var results = await Eval(json, A(AssertionKind.JsonPath, "items.1.name"));
        results[0].Passed.Should().BeTrue();
        results[0].ActualSnippet.Should().Be("second");
    }

    [Fact]
    public async Task JsonPath_Fail_MissingKey()
    {
        string json = """{"a":{"b":1}}""";
        var results = await Eval(json, A(AssertionKind.JsonPath, "a.x"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("not found").And.Contain("x");
    }

    [Fact]
    public async Task JsonPath_Fail_ArrayIndexOutOfRange()
    {
        string json = """{"items":[{"name":"only"}]}""";
        var results = await Eval(json, A(AssertionKind.JsonPath, "items.5.name"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("index");
    }

    [Fact]
    public async Task JsonPath_Fail_MalformedJson()
    {
        var results = await Eval("this is not json {", A(AssertionKind.JsonPath, "a.b"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("not valid JSON");
    }

    [Fact]
    public async Task JsonPath_Fail_NullValue()
    {
        string json = """{"a":null}""";
        var results = await Eval(json, A(AssertionKind.JsonPath, "a"));
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("null");
    }

    // ── ScoreMin ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ScoreMin_NullJudge_PassesWithNote()
    {
        var results = await Eval("some output", A(AssertionKind.ScoreMin, "is this good?", 0.8), judge: null);
        results[0].Passed.Should().BeTrue();
        results[0].FailReason.Should().Contain("judge unavailable");
    }

    [Fact]
    public async Task ScoreMin_Pass_WhenScoreMeetsThreshold()
    {
        // Judge returns QualityScore 9 -> normalised 0.9 >= threshold 0.8.
        var judge = new FakeJudge(qualityScore: 9);
        var results = await Eval("great output", A(AssertionKind.ScoreMin, "quality", 0.8), judge);
        results[0].Passed.Should().BeTrue();
    }

    [Fact]
    public async Task ScoreMin_Fail_WhenScoreBelowThreshold()
    {
        // Judge returns QualityScore 4 -> normalised 0.4 < threshold 0.8.
        var judge = new FakeJudge(qualityScore: 4);
        var results = await Eval("weak output", A(AssertionKind.ScoreMin, "quality", 0.8), judge);
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("below threshold");
    }

    [Fact]
    public async Task ScoreMin_Fail_WhenJudgeReturnsNull()
    {
        var judge = new FakeJudge(qualityScore: null);
        var results = await Eval("output", A(AssertionKind.ScoreMin, "quality", 0.5), judge);
        results[0].Passed.Should().BeFalse();
        results[0].FailReason.Should().Contain("no assessment");
    }

    // ── multiple assertions in one batch ─────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_EvaluatesAllAssertions_InOrder()
    {
        var assertions = new[]
        {
            A(AssertionKind.Contains, "alpha"),
            A(AssertionKind.NotContains, "omega"),
            A(AssertionKind.Regex, @"\d+")
        };
        var results = await SuiteAssertionEvaluator.EvaluateAsync("alpha 42 beta", assertions);
        results.Should().HaveCount(3);
        results.Should().OnlyContain(r => r.Passed);
    }

    /// <summary>
    /// Minimal fake <see cref="IInferService"/> used only by ScoreMin tests: <see cref="ToObject{T}"/> returns
    /// a canned <see cref="AnalysisResult"/> (or null). Every other member throws — they are never reached.
    /// </summary>
    private sealed class FakeJudge : IInferService
    {
        private readonly int? _qualityScore;
        public FakeJudge(int? qualityScore) => _qualityScore = qualityScore;

        public Task<T?> ToObject<T>(
            string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null,
            string? modelName = null, int retryAttempt = 0, int? originalRetryLimit = null,
            CancellationToken token = default)
        {
            if (_qualityScore is null)
                return Task.FromResult<T?>(default);

            object boxed = new AnalysisResult { QualityScore = _qualityScore.Value, FulfilledRequest = true };
            return Task.FromResult((T?)boxed);
        }

        public Task<T?> ToObject<T>(
            string promptName, Input? input, ModelProfile? modelProfile = null,
            string? modelName = null, CancellationToken token = default)
            => ToObject<T>(promptName, input is null ? null : new List<Input> { input }, modelProfile, modelName, token: token);

        // ── unused members ───────────────────────────────────────────────
        private static T Nope<T>() => throw new NotImplementedException();

        public Task<CompletionResult?> Completion(Prompt prompt, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken token = default, bool directRoute = false) => Nope<Task<CompletionResult?>>();
        public Task<CompletionResult?> Completion(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken token = default) => Nope<Task<CompletionResult?>>();
        public IAsyncEnumerable<string> CompletionStream(Prompt prompt, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, Type? outputType = null, CancellationToken cancellationToken = default, bool directRoute = false) => EmptyStream();
        public Task<(T? Value, CompletionResult? Usage)> ToObjectWithUsage<T>(string promptName, List<Input>? inputs, ModelProfile? model = null, string? modelName = null, CancellationToken ct = default) => Nope<Task<(T?, CompletionResult?)>>();
        public Task<TEnum> ToEnum<TEnum>(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, bool includeEnumValues = false, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default) where TEnum : struct, Enum => Nope<Task<TEnum>>();
        public Task<TEnum> ToEnum<TEnum>(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, bool includeEnumValues = false, CancellationToken token = default) where TEnum : struct, Enum => Nope<Task<TEnum>>();
        public Task<string?> ToString(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<string?>>();
        public Task<string?> ToString(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<string?>>();
        public Task<bool?> ToBool(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<bool?>>();
        public Task<bool?> ToBool(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<bool?>>();
        public Task<JObject?> ToJObject(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<JObject?>>();
        public Task<JObject?> ToJObject(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<JObject?>>();
        public Task<List<string>> ToStringList(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, int retryAttempt = 0, int? originalRetryLimit = null, CancellationToken token = default) => Nope<Task<List<string>>>();
        public Task<List<string>> ToStringList(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<List<string>>>();
        public Task<List<string>> ToStringListClean(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<List<string>>>();
        public Task<List<string>> ToStringListClean(string promptName, Input? input, ModelProfile? modelProfile = null, string? modelName = null, CancellationToken token = default) => Nope<Task<List<string>>>();
        public Task<List<string>> ToStringListLimited(string promptName, List<Input>? inputs = null, ModelProfile? modelProfile = null, string? modelName = null, int? maxLines = null, Func<string, bool>? evaluator = null, CancellationToken token = default) => Nope<Task<List<string>>>();
        public Task<List<string>> ToStringListLimited(string promptName, Input? input = null, ModelProfile? modelProfile = null, string? modelName = null, int? maxLines = null, Func<string, bool>? evaluator = null, CancellationToken token = default) => Nope<Task<List<string>>>();
        public Prompt FindPrompt(string name) => Nope<Prompt>();
        public string? ListInputs(ModelProfile model, List<Input>? inputs) => Nope<string?>();

        private static async IAsyncEnumerable<string> EmptyStream()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
