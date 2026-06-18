// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;
using Revi;
using LogLevel = Revi.LogLevel;

namespace ReviDotNet.Forge.Components.Pages.Workshop.Instance;

/// <summary>
/// Builds a synthetic but contract-faithful <see cref="RlogEvent"/> tree — exactly the shape
/// <see cref="AgentRunner"/> / <see cref="AgentReviLogger"/> emit (run-root → llm-request →
/// tool-call → tool-start/tool-result, sub-agent run-root nested under a tool-call, state/cycle
/// tags, transitions, an end event). Fed through <see cref="GroupedTraceBuilder"/> it drives the
/// session-view preview, exercising the live projection pipeline without needing a live agent run.
/// Represents a still-running deep-research run: plan ✓ → gather ✓ (with a high-fan-out scrape step,
/// a failure and a dropped call) → verify (running, delegating to a fact-checker sub-agent).
/// </summary>
public static class DummyTrace
{
    public const string SessionId = "b1c4e9af20d7";
    private const string Agent = "research/deep-research";

    public static List<RlogEvent> Build()
    {
        var b = new Builder(SessionId, Agent);

        // run-root
        string root = b.Start("plan",
            inputs: new Dictionary<string, object> { ["task"] = "Survey the current state of solid-state battery commercialization: gather sources, verify claims, and produce one cited report." },
            profile: new { name = Agent, version = 3, entryState = "plan", states = new[] { new { name = "plan", model = "gemini-2-5-flash" } } });

        // ── plan (cycle 1) ──
        string s1 = b.Llm(root, "plan", 1, step: 1);
        b.Thinking(s1, "plan", 1, "Break the task into gather → verify → synthesize. First find candidate sources.");
        b.Content(s1, "plan", 1, "I’ll search for sources, then fan out scraping and verification in parallel.");
        string c1 = b.ToolCall(s1, "plan", 1, "web-search", "{ \"q\": \"solid-state battery commercialization 2026\", \"limit\": 50 }");
        b.ToolStart(c1, "plan", 1, "web-search");
        b.ToolResult(c1, "plan", 1, "web-search", "50 candidate URLs across 8 domains.", failed: false);
        b.LlmResponse(s1, "plan", 1, inTok: 1100, outTok: 280);
        b.Transition(root, "plan", "gather", "READY");

        // ── gather (cycle 1) — high fan-out scrape step (cube grid) with a failure + a dropped call ──
        string s2 = b.Llm(root, "gather", 1, step: 2);
        b.Content(s2, "gather", 1, "Requested 7 fetches; the tool-call-limit capped it at 6.");
        for (int i = 1; i <= 6; i++)
        {
            bool fail = i == 3;
            string ci = b.ToolCall(s2, "gather", 1, "web-scrape", $"{{ \"url\": \"https://techreview.example.com/article-{i:00}\" }}");
            b.ToolStart(ci, "gather", 1, "web-scrape");
            b.ToolResult(ci, "gather", 1, "web-scrape",
                fail ? "HTTP 403 Forbidden — origin blocked the request." : $"Fetched {3 + i}.2 KB. \"Solid-state cells hit 500 Wh/kg in pilot production.\"",
                failed: fail);
        }
        b.ToolDropped(s2, "gather", 1, ("web-scrape", "{ \"url\": \"https://battery-news.example.com/article-07\" }"));
        b.LlmResponse(s2, "gather", 1, inTok: 4200, outTok: 600);
        b.Transition(root, "gather", "verify", "READY");

        // ── verify (cycle 1) — RUNNING: delegates to a fact-checker sub-agent + a still-running search ──
        string s3 = b.Llm(root, "verify", 1, step: 3);
        b.Content(s3, "verify", 1, "Delegating verification — one fact-checker sub-agent per claim cluster.");

        // sub-agent: invoke_agent tool-call with a nested run-root that completed
        string c3 = b.ToolCall(s3, "verify", 1, "invoke_agent", "{ \"agent\": \"research/fact-checker\", \"task\": \"verify claim cluster 1\" }");
        b.ToolStart(c3, "verify", 1, "invoke_agent");
        string subRoot = b.SubStart(c3, "research/fact-checker", "check", profile: new { name = "research/fact-checker", version = 2, entryState = "check" });
        string ss1 = b.Llm(subRoot, "check", 1, step: 1, session: "f00dcafe9911", depth: 1);
        b.Content(ss1, "check", 1, "Searching independent sources, one per claim.", session: "f00dcafe9911", depth: 1);
        string sc1 = b.ToolCall(ss1, "check", 1, "web-search", "{ \"q\": \"claim 1 independent verification\" }", session: "f00dcafe9911", depth: 1);
        b.ToolStart(sc1, "check", 1, "web-search", session: "f00dcafe9911", depth: 1);
        b.ToolResult(sc1, "check", 1, "web-search", "3 corroborating sources found.", failed: false, session: "f00dcafe9911", depth: 1);
        b.LlmResponse(ss1, "check", 1, inTok: 800, outTok: 150, session: "f00dcafe9911", depth: 1);
        b.SubEnd(subRoot, "check", "claim cluster 1 — corroborated", session: "f00dcafe9911", depth: 1);
        b.ToolResult(c3, "verify", 1, "invoke_agent", "claim cluster 1 — corroborated", failed: false);

        // a second tool call still in flight (no tool-result) → running
        string c4 = b.ToolCall(s3, "verify", 1, "web-search", "{ \"q\": \"claim 2 independent verification\" }");
        b.ToolStart(c4, "verify", 1, "web-search");

        // No llm-response on s3 and no run end → the run is still running (verify step is the tail).
        return b.Events;
    }

    /// <summary>Small helper that stamps ids/timestamps/tags and JSON-serialises objects the way the logger does.</summary>
    private sealed class Builder
    {
        public readonly List<RlogEvent> Events = new();
        private readonly string _session;
        private readonly string _agent;
        private DateTime _clock = DateTime.UtcNow.AddMinutes(-3);
        private int _seq;

        public Builder(string session, string agent) { _session = session; _agent = agent; }

        private string NextId() => $"e{++_seq:0000}";
        private DateTime Tick(int ms) { _clock = _clock.AddMilliseconds(ms); return _clock; }

        private static string Tags(string agent, string session, string step, string state, int cycle, int depth) =>
            string.Join(' ', $"agent:{agent}", $"agent-session:{session}", $"agent-step:{step}", $"agent-state:{state}", $"agent-cycle:{cycle}", $"agent-depth:{depth}");

        private string Add(string? parent, string identifier, string step, string state, int cycle, int depth, string session, string agent,
            object? o1 = null, string? o1n = null, object? o2 = null, string? o2n = null, LogLevel level = LogLevel.Info)
        {
            string id = NextId();
            Events.Add(new RlogEvent
            {
                Id = id,
                ParentId = parent,
                Timestamp = Tick(400),
                Level = level,
                Message = $"{identifier}",
                Identifier = identifier,
                Cycle = cycle,
                Tags = Tags(agent, session, step, state, cycle, depth),
                Object1 = o1 is null ? null : JsonConvert.SerializeObject(o1),
                Object1Name = o1n,
                Object2 = o2 is null ? null : JsonConvert.SerializeObject(o2),
                Object2Name = o2n,
            });
            return id;
        }

        public string Start(string entryState, Dictionary<string, object> inputs, object profile) =>
            Add(null, "agent-run-start", "start", entryState, 0, 0, _session, _agent, inputs, "inputs", profile, "profile");

        public string SubStart(string parentToolCall, string agent, string entryState, object profile) =>
            Add(parentToolCall, "agent-run-start", "start", entryState, 0, 1, "f00dcafe9911", agent, new Dictionary<string, object> { ["task"] = "verify" }, "inputs", profile, "profile");

        public string Llm(string parent, string state, int cycle, int step, string? session = null, int depth = 0) =>
            Add(parent, AgentReviLogger.Step.LlmRequest, "llm-request", state, cycle, depth, session ?? _session, depth > 0 ? "research/fact-checker" : _agent,
                o1: new[] { new { role = "user", content = "…" } }, o1n: "messages");

        public void Thinking(string parent, string state, int cycle, string text, string? session = null, int depth = 0) =>
            Add(parent, AgentReviLogger.Step.Thinking, "thinking", state, cycle, depth, session ?? _session, depth > 0 ? "research/fact-checker" : _agent, text, "thinking");

        public void Content(string parent, string state, int cycle, string text, string? session = null, int depth = 0) =>
            Add(parent, AgentReviLogger.Step.Content, "content", state, cycle, depth, session ?? _session, depth > 0 ? "research/fact-checker" : _agent, text, "content");

        public string ToolCall(string parent, string state, int cycle, string tool, string input, string? session = null, int depth = 0) =>
            Add(parent, AgentReviLogger.Step.ToolCall, "tool-call", state, cycle, depth, session ?? _session, depth > 0 ? "research/fact-checker" : _agent, input, "input", tool, "tool");

        public void ToolStart(string parent, string state, int cycle, string tool, string? session = null, int depth = 0) =>
            Add(parent, AgentReviLogger.Step.ToolStart, "tool-start", state, cycle, depth, session ?? _session, depth > 0 ? "research/fact-checker" : _agent);

        public void ToolResult(string parent, string state, int cycle, string tool, string output, bool failed, string? session = null, int depth = 0) =>
            Add(parent, AgentReviLogger.Step.ToolResult, "tool-result", state, cycle, depth, session ?? _session, depth > 0 ? "research/fact-checker" : _agent,
                output, "output", new { failed, error = failed ? "blocked" : (string?)null }, "status", failed ? LogLevel.Warning : LogLevel.Info);

        public void ToolDropped(string parent, string state, int cycle, params (string name, string input)[] dropped) =>
            Add(parent, AgentReviLogger.Step.ToolDropped, "tool-dropped", state, cycle, 0, _session, _agent,
                dropped.Select(d => new { name = d.name, input = d.input }).ToList(), "dropped", level: LogLevel.Warning);

        public void LlmResponse(string parent, string state, int cycle, int inTok, int outTok, string? session = null, int depth = 0) =>
            Add(parent, AgentReviLogger.Step.LlmResponse, "llm-response", state, cycle, depth, session ?? _session, depth > 0 ? "research/fact-checker" : _agent,
                "{…}", "raw", new { inputTokens = inTok, outputTokens = outTok, model = "gemini-2-5-flash" }, "meta");

        public void Transition(string root, string from, string to, string signal) =>
            Add(root, AgentReviLogger.Step.StateTransition, "state-transition", from, 1, 0, _session, _agent,
                new { from, to, signal }, "transition", new[] { from, to }, "history");

        public void SubEnd(string subRoot, string state, string finalOutput, string session, int depth) =>
            Add(subRoot, AgentReviLogger.Step.End, "end", state, 1, depth, session, "research/fact-checker",
                finalOutput, "final-output", new { exitReason = "Completed", totalSteps = 1 }, "meta");
    }
}
