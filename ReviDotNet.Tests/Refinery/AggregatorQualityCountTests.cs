// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Refinery;

/// <summary>
/// The aggregate must expose how many runs were actually judge-scored vs how many expected a score and
/// got none. In campaign 1e13eae1 all 46 judge verdicts failed to parse and the campaign surface showed
/// only "quality 0.00" — indistinguishable from a genuinely bad agent. These counts make a broken judge
/// look like a broken judge.
/// </summary>
public class AggregatorQualityCountTests
{
    private static ScoreCard Card(string id, bool rubric, QualityScore? quality) => new()
    {
        ScenarioId = id,
        AgentName = "chatbot",
        QualityExpected = rubric,
        Quality = quality
    };

    [Fact]
    public void Counts_scored_runs_and_judge_failures_separately()
    {
        SuiteAggregate agg = Aggregator.Aggregate(
        [
            Card("s1", rubric: true,  quality: new QualityScore { Overall = 8 }),
            Card("s2", rubric: true,  quality: null),                             // judge failed / unparsed
            Card("s3", rubric: true,  quality: new QualityScore { Overall = 6 }),
            Card("s4", rubric: false, quality: null)                              // no rubric — not a failure
        ]);

        agg.QualityScoredRuns.Should().Be(2);
        agg.QualityJudgeFailures.Should().Be(1, "only runs that EXPECTED a score count as judge failures");
        agg.RunCount.Should().Be(4);
        agg.QualityMean.Should().Be(7);
    }

    [Fact]
    public void All_verdicts_missing_is_visible_not_just_zero_quality()
    {
        SuiteAggregate agg = Aggregator.Aggregate(
        [
            Card("s1", rubric: true, quality: null),
            Card("s2", rubric: true, quality: null)
        ]);

        agg.QualityScoredRuns.Should().Be(0);
        agg.QualityJudgeFailures.Should().Be(2,
            "the 46/46-unparsed-verdicts failure mode must be readable straight off the aggregate");
        agg.QualityMean.Should().Be(0);
    }
}
