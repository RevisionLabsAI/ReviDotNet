using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task SelectionIsCachedAcrossStepsOfOneStateActivation()
    {
        using AgentTestHarness harness = new(
            [new FakeAgentTurn("CONTINUE", [], "step one"), new FakeAgentTurn("CONTINUE", [], "step two"), new FakeAgentTurn("DONE", [], "done")],
            model => AgentBuilder.FromText($"""
            [[information]]
            name = selector-cache-test
            [[loop]]
            entry = work
            [[state.work]]
            model = {model}
            tools = required, optional
            tool-selector = context-select
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
        tools.Register(new TestTool("required", "REQUIRED_MARKER")); tools.Register(new TestTool("optional", "OPTIONAL_MARKER"));
        CountingSelector selector = new();
        AgentRunner runner = new(harness.Agent, [], default, new(), models, provider.GetRequiredService<IPromptManager>(), tools, contextSelector: selector);
        AgentResult result = await runner.RunAsync();
        result.ExitReason.Should().Be(AgentExitReason.Completed);
        harness.Requests.Should().HaveCount(3);
        // The selector sees the request and state only, so every step of one activation shares one decision.
        selector.Calls.Should().Be(1);
    }

    [Fact]
    public async Task FileToolsTheToolManagerCannotRunAreNeitherAdvertisedNorAllowed()
    {
        using AgentTestHarness harness = new(
            [new FakeAgentTurn("CONTINUE", [("document-search", "{\"query\":\"hello\"}")], "searching"), new FakeAgentTurn("DONE", [], "done")],
            model => AgentBuilder.FromText($"""
            [[information]]
            name = file-tools-test
            [[loop]]
            entry = work
            [[state.work]]
            model = {model}
            [[_state.work.instruction]]
            Read the attachments.
            [[_loop]]
            work
              -> self [when: CONTINUE]
              -> [end] [when: DONE]
            """)!);
        ServiceCollection services = new(); services.AddReviDotNet(typeof(DecisionAgentTests).Assembly);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IModelManager models = provider.GetRequiredService<IModelManager>(); models.Add(harness.Model);
        // Built the way a host builds a manager without a document search service (Forge's per-run manager did).
        ToolManagerService tools = new(new Lazy<IAgentService>(provider.GetRequiredService<IAgentService>),
            provider.GetRequiredService<IWebContentService>(), models, new RecordingReviLogger<ToolManagerService>());
        SessionFileRegistry files = new([new SessionFile { Id = "f1", Name = "notes.txt", MediaType = "text/plain", Bytes = Encoding.UTF8.GetBytes("hello") }]);
        AgentRunner runner = new(harness.Agent, [], default, AgentRunContext.Root(files), models, provider.GetRequiredService<IPromptManager>(), tools);
        AgentResult result = await runner.RunAsync();
        result.ExitReason.Should().Be(AgentExitReason.Completed);
        string[] requests = harness.Requests.ToArray();
        requests[0].Should().Contain("read-file").And.Contain("search-files").And.NotContain("document-search");
        requests[1].Should().NotContain("is not registered");
    }

    /// <summary>Counts selections; its cache key covers everything it is sent, like the decision-backed selector's.</summary>
    private sealed class CountingSelector : IContextSelector
    {
        public int Calls;
        public string? GetCacheKey(object state, IReadOnlyList<ContextCandidate> candidates, ContextSelectionOptions options) =>
            JsonSerializer.Serialize(new { state, candidates, options });
        public Task<ContextSelectionResult> SelectAsync(object state, IReadOnlyList<ContextCandidate> candidates, ContextSelectionOptions options, CancellationToken token = default)
        {
            Calls++;
            return Task.FromResult(new ContextSelectionResult(candidates, null, null));
        }
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
