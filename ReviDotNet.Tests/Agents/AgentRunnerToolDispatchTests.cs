// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Regression tests for the parallel-tool-dispatch changes:
///   • #1 tool-call events nest under their step's llm-request event
///   • #2 max-parallel-tools bounds concurrent execution (with tool-start marking queued→running)
///   • #4 over-the-tool-call-limit calls are dropped and emit a tool-dropped event
///   • #6 the assistant content is emitted as its own event
/// </summary>
public class AgentRunnerToolDispatchTests
{
    // ── #2: concurrency is bounded by the max-parallel-tools guardrail ──

    [Fact]
    public async Task MaxParallelTools_BoundsConcurrentToolExecution()
    {
        var probe = new ConcurrencyProbeTool();
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", ProbeCalls(5), "probing") },
            _ => AgentBuilder.FromText(AgentText("max-parallel-tools = 2"))!);
        harness.RegisterTool(probe);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        probe.TotalExecutions.Should().Be(5);                       // all allowed calls ran
        probe.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(2); // …but never more than 2 at once
    }

    [Fact]
    public async Task Tools_RunInParallel_WhenUnbounded()
    {
        // Contrast with the capped test: with no cap the same five calls overlap freely,
        // proving the gate (not accidental serialization) is what limits the capped case.
        var probe = new ConcurrencyProbeTool();
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", ProbeCalls(5), "probing") },
            _ => AgentBuilder.FromText(AgentText("tool-call-limit = 50"))!);
        harness.RegisterTool(probe);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        probe.TotalExecutions.Should().Be(5);
        probe.MaxObservedConcurrency.Should().BeGreaterThan(2);
    }

    // ── #4: excess calls over tool-call-limit are dropped and surfaced as an event ──

    [Fact]
    public async Task ToolCallLimit_DropsExcessCalls_AndEmitsToolDroppedEvent()
    {
        var probe = new ConcurrencyProbeTool();
        var logger = new RecordingReviLogger();
        using (UseLogger(logger))
        using (var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", ProbeCalls(4), "probing") },
            _ => AgentBuilder.FromText(AgentText("tool-call-limit = 2"))!))
        {
            harness.RegisterTool(probe);

            AgentResult result = await Agent.Run(harness.AgentName);

            result.ExitReason.Should().Be(AgentExitReason.Completed);
            probe.TotalExecutions.Should().Be(2);   // 2 of 4 ran; the other 2 were dropped
            Identifiers(logger, harness.AgentName).Should().Contain("tool-dropped");
        }
    }

    // ── #1 + #6 + #2: content/tool-start events emitted; tool-call nested under llm-request ──

    [Fact]
    public async Task Step_EmitsContentAndToolStart_AndNestsToolCallUnderRequest()
    {
        var logger = new RecordingReviLogger();
        using (UseLogger(logger))
        using (var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", ProbeCalls(1), "Here is my message.") },
            _ => AgentBuilder.FromText(AgentText(""))!))
        {
            harness.RegisterTool(new ConcurrencyProbeTool());

            AgentResult result = await Agent.Run(harness.AgentName);
            result.ExitReason.Should().Be(AgentExitReason.Completed);

            List<Rlog> mine = logger.For(harness.AgentName).ToList();
            mine.Select(e => e.Identifier).Should()
                .Contain(new[] { "content", "tool-call", "tool-start", "tool-result" });

            // #1: the tool-call hangs off this step's llm-request, not the run-root.
            Rlog toolCall = mine.First(e => e.Identifier == "tool-call");
            toolCall.Parent.Should().NotBeNull();
            toolCall.Parent!.Identifier.Should().Be("llm-request");

            // #2: tool-start (queued→running) nests under its tool-call.
            Rlog toolStart = mine.First(e => e.Identifier == "tool-start");
            toolStart.Parent!.Identifier.Should().Be("tool-call");
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static (string, string)[] ProbeCalls(int n) =>
        Enumerable.Range(0, n).Select(i => ("probe", "{\"i\":" + i + "}")).ToArray();

    private static string AgentText(string guardrails)
    {
        string g = string.IsNullOrWhiteSpace(guardrails)
            ? ""
            : "\n[[state.work.guardrails]]\n" + guardrails + "\n";
        return $@"
[[information]]
name = unused

[[loop]]
entry = work

[[state.work]]
description = work state
tools = probe
{g}
[[_state.work.instruction]]
Call the probe tool, then signal DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";
    }

    private static IEnumerable<string> Identifiers(RecordingReviLogger logger, string agentName) =>
        logger.For(agentName).Select(e => e.Identifier);

    /// <summary>Registers <paramref name="logger"/> as the ambient IReviLogger; the returned
    /// scope restores the no-logger default on dispose.</summary>
    private static IDisposable UseLogger(RecordingReviLogger logger)
    {
        ReviServiceLocator.SetProvider(new SingleService(typeof(IReviLogger), logger));
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => ReviServiceLocator.SetProvider(null!);
    }

    /// <summary>Minimal IServiceProvider exposing a single service (avoids a DI-container dependency).</summary>
    private sealed class SingleService : IServiceProvider
    {
        private readonly Type _type;
        private readonly object _instance;
        public SingleService(Type type, object instance) { _type = type; _instance = instance; }
        public object? GetService(Type serviceType) => serviceType == _type ? _instance : null;
    }

    /// <summary>An IBuiltInTool that records the peak number of overlapping executions.</summary>
    private sealed class ConcurrencyProbeTool : IBuiltInTool
    {
        private int _current;
        private int _max;
        private int _total;
        private readonly int _delayMs;

        public ConcurrencyProbeTool(int delayMs = 60) { _delayMs = delayMs; }

        public string Name => "probe";
        public string Description => "Concurrency probe (test).";

        public int MaxObservedConcurrency => Volatile.Read(ref _max);
        public int TotalExecutions => Volatile.Read(ref _total);

        public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
        {
            Interlocked.Increment(ref _total);
            int cur = Interlocked.Increment(ref _current);
            // _max = max(_max, cur), lock-free
            int observed;
            while (cur > (observed = Volatile.Read(ref _max)) &&
                   Interlocked.CompareExchange(ref _max, cur, observed) != observed) { }
            try { await Task.Delay(_delayMs, token); }   // hold the slot so overlap is observable
            finally { Interlocked.Decrement(ref _current); }
            return new ToolCallResult { ToolName = Name, Output = "ok" };
        }
    }

    /// <summary>Captures every emitted event so tests can assert on identifiers and parentage.</summary>
    private sealed class RecordingReviLogger : IReviLogger
    {
        public ConcurrentQueue<Rlog> Events { get; } = new();

        public IEnumerable<Rlog> For(string agentName) =>
            Events.Where(e => e.Tags != null && e.Tags.Contains("agent:" + agentName.ToLowerInvariant()));

        private Rlog Record(Rlog? parent, LogLevel level, string message, string? identifier, int cycle, string? tags)
        {
            var r = new Rlog(parent, level, message, identifier, cycle, tags);
            Events.Enqueue(r);
            return r;
        }

        public Rlog Log(Rlog? parent, LogLevel level, string message, string? identifier = "", int cycle = 0,
            string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null,
            string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(parent, level, message, identifier, cycle, tags);

        public Rlog LogInfo(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(null, LogLevel.Info, message, identifier, cycle, tags);
        public Rlog LogInfo(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(parent, LogLevel.Info, message, identifier, cycle, tags);

        public Rlog LogDebug(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(null, LogLevel.Debug, message, identifier, cycle, tags);
        public Rlog LogDebug(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(parent, LogLevel.Debug, message, identifier, cycle, tags);

        public Rlog LogWarning(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(null, LogLevel.Warning, message, identifier, cycle, tags);
        public Rlog LogWarning(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(parent, LogLevel.Warning, message, identifier, cycle, tags);

        public Rlog LogError(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(null, LogLevel.Error, message, identifier, cycle, tags);
        public Rlog LogError(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(parent, LogLevel.Error, message, identifier, cycle, tags);

        public Rlog LogFatal(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(null, LogLevel.Fatal, message, identifier, cycle, tags);
        public Rlog LogFatal(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null,
            string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(parent, LogLevel.Fatal, message, identifier, cycle, tags);

        public Task DumpLog(System.Text.StringBuilder sb, string fileNamePrefix) => Task.CompletedTask;
        public Task DumpLog(string? textToDump, string fileNamePrefix, Rlog? record = null) => Task.CompletedTask;
        public Task DumpImage(byte[] imageBytes, string fileNamePrefix, string extension = "png") => Task.CompletedTask;
        public bool IsEnabled(LogLevel level) => true;
    }
}
