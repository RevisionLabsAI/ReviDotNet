// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Exercises the shape of the <c>research/web-report</c> agent: an <c>analyze</c> state that scrapes
/// every provided URL and an <c>report</c> state that synthesizes a single Markdown summary. The loop
/// graph (analyze → CONTINUE self / READY → report → DONE) and tool gating are driven with a scripted
/// fake model and a fake <c>web-scrape</c> tool, so this is deterministic and makes no network calls.
/// </summary>
public class WebReportAgentTests
{
    // Mirrors RConfigs/Agents/research/web-report.agent. Model overrides are intentionally omitted so
    // the AgentTestHarness can pin every state to its fake model (see AgentTestHarness ctor).
    private const string AgentText = """
        [[information]]
        name = web-report

        [[loop]]
        entry = analyze

        [[state.analyze]]
        description = Fetch and read each URL with web-scrape.
        tools = web-scrape web-extract

        [[state.analyze.guardrails]]
        max-steps = 12
        tool-call-limit = 12
        loop-detection = true

        [[state.report]]
        description = Synthesize one Markdown report.
        tools =

        [[state.report.guardrails]]
        max-steps = 3
        loop-detection = true

        [[_state.analyze.instruction]]
        Scrape every URL in the task. Emit CONTINUE while pages remain, READY once all are fetched.

        [[_state.report.instruction]]
        Write the final Markdown report from the gathered pages and emit DONE.

        [[_loop]]
        analyze
          -> analyze [when: CONTINUE]
          -> report [when: READY]
          -> [end] [when: ABORT]
        report
          -> [end] [when: DONE]
        """;

    [Fact]
    public async Task FetchesEveryUrl_ThenProducesReport()
    {
        const string urlA = "https://a.example/post";
        const string urlB = "https://b.example/post";
        const string report = "# Web Page Report\n\n## A (https://a.example/post)\n- alpha";

        var script = new[]
        {
            // analyze: request both pages in one step, stay (CONTINUE) to read the results.
            new FakeAgentTurn("CONTINUE",
                new[] { ("web-scrape", urlA), ("web-scrape", urlB) },
                "Fetching the two pages."),
            // analyze: every URL fetched -> advance to report.
            new FakeAgentTurn("READY", new (string, string)[0], "Both pages read; ready to write."),
            // report: emit the final Markdown and finish.
            new FakeAgentTurn("DONE", new (string, string)[0], report)
        };

        var scraped = new ConcurrentBag<string>();

        using var harness = new AgentTestHarness(script, _ => AgentBuilder.FromText(AgentText)!);
        harness.RegisterTool(new FakeBuiltInTool("web-scrape", input =>
        {
            scraped.Add(input);
            return $"# Page for {input}\n\nSome clean markdown body.";
        }));
        harness.RegisterTool(new FakeBuiltInTool("web-extract", "{\"chunks\":[]}"));

        AgentResult result = await Agent.Run(harness.AgentName, new Dictionary<string, object>
        {
            ["task"] = $"Summarize these pages: {urlA} {urlB}"
        });

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be(report);
        result.StateHistory.Should().Equal("analyze", "report");
        scraped.Should().BeEquivalentTo(new[] { urlA, urlB },
            "the analyze state must scrape every URL it is given");
    }

    [Fact]
    public async Task ReportState_CannotCallTools()
    {
        // The report state declares no tools; a stray tool call from the model must be ignored.
        var script = new[]
        {
            new FakeAgentTurn("READY", new (string, string)[0], "nothing to fetch"),
            new FakeAgentTurn("DONE", new[] { ("web-scrape", "https://late.example") }, "final report")
        };

        bool scrapedDuringReport = false;

        using var harness = new AgentTestHarness(script, _ => AgentBuilder.FromText(AgentText)!);
        harness.RegisterTool(new FakeBuiltInTool("web-scrape", _ => { scrapedDuringReport = true; return ""; }));

        AgentResult result = await Agent.Run(harness.AgentName, new Dictionary<string, object>
        {
            ["task"] = "no urls here"
        });

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        result.FinalOutput.Should().Be("final report");
        scrapedDuringReport.Should().BeFalse("the report state gates out all tools");
    }
}
