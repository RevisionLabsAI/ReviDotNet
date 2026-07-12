// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Linq;
using System.Text;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Inference;

/// <summary>
/// Tests for the loop-detection circuit breaker. The detector must catch genuine degenerate loops
/// (char runs, phrase loops, line loops) while NEVER tripping on legitimately repetitive output —
/// tables, JSON arrays of similar objects, code boilerplate, ordinary prose. False positives kill
/// real answers, so the false-positive corpus is the more important half of this file.
/// </summary>
public class RepetitionDetectorTests
{
    private const string Algo = "repeat-512";

    /// <summary>Realistic non-looping prefix so detection can't lean on the text being short.</summary>
    private static readonly string Prose = string.Join(" ",
        Enumerable.Range(0, 40).Select(i => $"Sentence {i} discusses a distinct aspect of the policy landscape."));

    // ── Algorithm parsing ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_algorithm_means_off(string? algo)
    {
        RepetitionDetector.TryParseAlgorithm(algo, out _).Should().BeFalse();
        RepetitionDetector.TryDetect(new string('x', 10_000), algo, out _).Should().BeFalse();
    }

    [Fact]
    public void Repeat_algorithm_parses_its_threshold()
    {
        RepetitionDetector.TryParseAlgorithm("repeat-512", out int n).Should().BeTrue();
        n.Should().Be(512);
        RepetitionDetector.TryParseAlgorithm("REPEAT-1024", out int n2).Should().BeTrue();
        n2.Should().Be(1024);
    }

    [Theory]
    [InlineData("repeat-")]
    [InlineData("repeat-0")]
    [InlineData("repeat--5")]
    [InlineData("ngram-512")]
    public void Unknown_or_malformed_algorithm_throws_loudly(string algo)
    {
        Action act = () => RepetitionDetector.TryParseAlgorithm(algo, out _);
        act.Should().Throw<FormatException>("a typo in a model config must fail loudly, not silently disable the breaker");
    }

    // ── True loops: must trip ───────────────────────────────────────────────────────

    [Fact]
    public void Trips_on_a_repeated_phrase_loop()
    {
        string text = Prose + " " + string.Concat(Enumerable.Repeat("the answer is clear and ", 40));
        RepetitionDetector.TryDetect(text, Algo, out string evidence).Should().BeTrue();
        evidence.Should().Contain("trailing loop");
    }

    [Fact]
    public void Trips_on_a_repeated_line_loop()
    {
        string line = "- I cannot provide additional information about that topic.\n";
        string text = Prose + "\n" + string.Concat(Enumerable.Repeat(line, 20));
        RepetitionDetector.TryDetect(text, Algo, out _).Should().BeTrue();
    }

    [Fact]
    public void Trips_on_a_single_character_run()
    {
        RepetitionDetector.TryDetect(Prose + new string('a', 600), Algo, out string evidence).Should().BeTrue();
        evidence.Should().Contain("unit of 1 char");
    }

    [Fact]
    public void Trips_even_when_the_loop_varies_only_in_whitespace()
    {
        // "unit\n unit \n\nunit" — identical after whitespace normalization.
        string unit = "The senator repeated the same claim.";
        StringBuilder sb = new(Prose);
        for (int i = 0; i < 30; i++)
            sb.Append(i % 2 == 0 ? "\n  " : " \n\n").Append(unit);
        RepetitionDetector.TryDetect(sb.ToString(), Algo, out _).Should().BeTrue();
    }

    [Fact]
    public void Reports_the_smallest_repeating_unit()
    {
        string text = Prose + " " + string.Concat(Enumerable.Repeat("ab", 400));
        RepetitionDetector.TryDetect(text, Algo, out string evidence).Should().BeTrue();
        evidence.Should().Contain("unit of 2 char(s)", "\"ab\" ×400 must not be reported as \"abab\" ×200");
    }

    [Fact]
    public void A_recovered_loop_mid_output_does_not_trip()
    {
        // The model looped briefly, then produced a real ending — suffix-anchored detection must pass this.
        string text = Prose + " " + string.Concat(Enumerable.Repeat("wait, ", 120)) + Prose;
        RepetitionDetector.TryDetect(text, Algo, out _).Should().BeFalse();
    }

    // ── False-positive corpus: must NEVER trip ──────────────────────────────────────

    [Fact]
    public void Does_not_trip_on_ordinary_prose()
    {
        RepetitionDetector.TryDetect(Prose + " " + Prose.Replace("policy", "budget"), Algo, out _).Should().BeFalse();
    }

    [Fact]
    public void Does_not_trip_on_a_markdown_table()
    {
        StringBuilder sb = new(Prose + "\n\n| Issue | Position | Votes |\n| --- | --- | --- |\n");
        for (int i = 0; i < 40; i++)
            sb.Append($"| Issue {i} | Support level {i % 7} | {i * 3} |\n");
        RepetitionDetector.TryDetect(sb.ToString(), Algo, out string ev).Should().BeFalse(
            $"table rows share structure but differ in content; evidence: {ev}");
    }

    [Fact]
    public void Does_not_trip_on_a_json_array_of_similar_objects()
    {
        StringBuilder sb = new(Prose + "\n[");
        for (int i = 0; i < 50; i++)
            sb.Append($"{{\"id\": {i}, \"name\": \"scenario-{i}\", \"passed\": {(i % 2 == 0 ? "true" : "false")}}},");
        sb.Append(']');
        RepetitionDetector.TryDetect(sb.ToString(), Algo, out string ev).Should().BeFalse(
            $"JSON rows differ by index; evidence: {ev}");
    }

    [Fact]
    public void Does_not_trip_on_code_boilerplate()
    {
        StringBuilder sb = new(Prose + "\n");
        for (int i = 0; i < 30; i++)
            sb.Append($"    public int Property{i} {{ get; set; }}\n");
        RepetitionDetector.TryDetect(sb.ToString(), Algo, out _).Should().BeFalse();
    }

    [Fact]
    public void Does_not_trip_on_a_short_refrain()
    {
        // A poem-style refrain repeated a handful of times spans well under the threshold.
        string text = Prose + "\n" + string.Concat(Enumerable.Repeat("And miles to go before I sleep.\n", 3));
        RepetitionDetector.TryDetect(text, Algo, out _).Should().BeFalse();
    }

    [Fact]
    public void Does_not_trip_below_the_minimum_output_floor()
    {
        // Even a pure loop must not trip before MinOutputChars of total output exist.
        string text = string.Concat(Enumerable.Repeat("ha ", 60)); // 180 chars, pure loop
        RepetitionDetector.TryDetect(text, "repeat-64", out _).Should().BeFalse();
    }
}
