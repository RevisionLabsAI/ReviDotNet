using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Revi;
using Revi.Tests.Helpers;
using ReviDotNet.Tests.Agents;
using Xunit;

namespace ReviDotNet.Tests.Decisions;

/// <summary>Real agent-loop checks with a local scripted inference server; no external provider calls.</summary>
public sealed class DecisionAgentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectorCannotExpandExecutionPermissionsAndFailureRestoresFullGuide(bool fallback)
    {
        using AgentTestHarness harness = new(
            [new FakeAgentTurn("CONTINUE", [("forbidden", "attempt")], "working"), new FakeAgentTurn("DONE", [], "done")],
            model => AgentBuilder.FromText($"""
            [[information]]
            name = selector-test
            [[loop]]
            entry = work
            [[state.work]]
            model = {model}
            tools = required, optional, hidden
            tool-selector = context-select
            always-visible-tools = required
            [[_state.work.instruction]]
            Use authorized tools.
            [[_loop]]
            work
              -> self [when: CONTINUE]
              -> [end] [when: DONE]
            """)!);
        ServiceCollection services = new(); services.AddReviDotNet(typeof(DecisionAgentTests).Assembly);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IModelManager models = provider.GetRequiredService<IModelManager>(); models.Add(harness.Model);
        IToolManager tools = provider.GetRequiredService<IToolManager>();
        TestTool forbidden = new("forbidden", "NEVER_VISIBLE_MARKER");
        tools.Register(forbidden); tools.Register(new TestTool("required", "REQUIRED_MARKER"));
        tools.Register(new TestTool("optional", "OPTIONAL_MARKER")); tools.Register(new TestTool("hidden", "HIDDEN_MARKER"));
        AgentRunner runner = new(harness.Agent, [], default, new(), models, provider.GetRequiredService<IPromptManager>(), tools,
            contextSelector: new InjectedSelector(fallback));
        AgentResult result = await runner.RunAsync();
        result.ExitReason.Should().Be(AgentExitReason.Completed);
        forbidden.Calls.Should().Be(0);
        string first = harness.Requests.First();
        first.Should().Contain("REQUIRED_MARKER").And.Contain("OPTIONAL_MARKER").And.NotContain("NEVER_VISIBLE_MARKER");
        if (fallback) first.Should().Contain("HIDDEN_MARKER"); else first.Should().NotContain("HIDDEN_MARKER");
    }

    private sealed class InjectedSelector(bool fallback) : IContextSelector
    {
        public Task<ContextSelectionResult> SelectAsync(object state, IReadOnlyList<ContextCandidate> candidates, ContextSelectionOptions options, CancellationToken token = default) =>
            Task.FromResult(new ContextSelectionResult([new("optional", ""), new("forbidden", "")], fallback ? "test-failure" : null, null));
    }
    private sealed class TestTool(string name, string description) : IBuiltInTool
    {
        public string Name => name;
        public string Description => description;
        public int Calls;
        public Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token) { Calls++; return Task.FromResult(new ToolCallResult { ToolName = Name, Output = "done" }); }
    }
}
