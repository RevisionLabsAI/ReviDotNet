// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Globalization;
using Newtonsoft.Json.Linq;
using Revi;

namespace ReviDotNet.Forge.Components.Pages.Workshop.Instance;

/// <summary>
/// Projects a flat <see cref="RlogEvent"/> list (the run-root → llm-request → tool-call tree, linked
/// by ParentId) into the <see cref="GroupedInstanceData"/> the grouped session view renders. Works
/// for both a completed/persisted run and a partial, still-streaming one (the latest step shows as
/// running, in-flight tool calls as running). Sub-agents (a nested run-root under a tool-call) are
/// projected recursively as the call's mini-instance steps.
/// </summary>
public static class GroupedTraceBuilder
{
    /// <summary>
    /// Builds the grouped view-model for one run session, or null if no run-root is present yet.
    /// </summary>
    public static GroupedInstanceData? Build(IReadOnlyList<RlogEvent> events, string sessionId, string? agentName = null)
    {
        if (events is null || events.Count == 0) return null;

        var byId = new Dictionary<string, RlogEvent>(StringComparer.Ordinal);
        foreach (var e in events)
            if (e.Id is { } id) byId[id] = e;

        var children = new Dictionary<string, List<RlogEvent>>(StringComparer.Ordinal);
        foreach (var e in events)
            if (e.ParentId is { } pid)
                (children.TryGetValue(pid, out var list) ? list : children[pid] = new()).Add(e);
        foreach (var list in children.Values)
            list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // The top run-root: an "agent-run-start" with no parent in this set. Prefer the one whose
        // session tag matches; otherwise the earliest top-level run-root.
        var roots = events
            .Where(e => e.Identifier == "agent-run-start" && (e.ParentId is null || !byId.ContainsKey(e.ParentId)))
            .OrderBy(e => e.Timestamp)
            .ToList();
        var root = roots.FirstOrDefault(r => string.Equals(Tag(r, "agent-session"), sessionId, StringComparison.Ordinal))
                   ?? roots.FirstOrDefault()
                   ?? events.Where(e => e.Identifier == "agent-run-start").OrderBy(e => e.Timestamp).FirstOrDefault();
        if (root is null) return null;

        RunSteps run = ProjectSteps(root, children);

        // Group consecutive steps sharing a (state, per-state-cycle) tag into activations, numbered
        // run-globally (1, 2, 3 …) per the chosen display convention.
        var drafts = new List<ActivationDraft>();
        string? curKey = null;
        ActivationDraft? cur = null;
        foreach (var (view, ev) in run.Steps)
        {
            string state = Tag(ev, "agent-state") ?? "state";
            string key = state + "#" + (Tag(ev, "agent-cycle") ?? "0");
            if (cur is null || key != curKey)
            {
                curKey = key;
                cur = new ActivationDraft { State = state, Cycle = drafts.Count + 1 };
                drafts.Add(cur);
            }
            cur.Steps.Add(view);
        }

        // Attach the closing transition (signal + next state) to each activation in order.
        var transitions = ChildrenOf(root, children).Where(e => e.Identifier == AgentReviLogger.Step.StateTransition).ToList();
        int transitionIdx = 0;
        foreach (var d in drafts)
        {
            if (transitionIdx >= transitions.Count) break;
            var tr = JObj(transitions[transitionIdx].Object1);
            if (tr is not null && string.Equals((string?)tr["from"], d.State, StringComparison.OrdinalIgnoreCase))
            {
                d.EndSignal = (string?)tr["signal"];
                d.NextState = (string?)tr["to"];
                transitionIdx++;
            }
        }

        var activations = drafts.Select(d => new ActivationView
        {
            State = d.State,
            Cycle = d.Cycle,
            Status = ActivationStatus(d.Steps),
            EndSignal = d.EndSignal,
            NextState = d.NextState,
            Steps = d.Steps,
        }).ToList();

        return new GroupedInstanceData
        {
            Meta = BuildMeta(root, run, events, sessionId, agentName),
            Activations = activations
        };
    }

    // ── per-run step projection (shared by the top run and each sub-agent) ───────────────────────

    private sealed class RunSteps
    {
        public required List<(StepView View, RlogEvent Ev)> Steps { get; init; }
        public bool Ended { get; init; }
        public bool ExitCompleted { get; init; }
        public RlogEvent? End { get; init; }
    }

    private static RunSteps ProjectSteps(RlogEvent root, Dictionary<string, List<RlogEvent>> children)
    {
        var kids = ChildrenOf(root, children);
        var stepEvents = kids.Where(e => e.Identifier == AgentReviLogger.Step.LlmRequest).OrderBy(e => e.Timestamp).ToList();
        var end = kids.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.End);
        bool ended = end is not null;
        bool exitCompleted = ended && string.Equals((string?)JObj(end!.Object2)?["exitReason"], "Completed", StringComparison.OrdinalIgnoreCase);
        DateTime lastTime = end?.Timestamp ?? MaxTimestamp(root, children);

        var steps = new List<(StepView, RlogEvent)>(stepEvents.Count);
        for (int i = 0; i < stepEvents.Count; i++)
        {
            var ev = stepEvents[i];
            DateTime next = i + 1 < stepEvents.Count ? stepEvents[i + 1].Timestamp : lastTime;
            long durMs = Math.Max(0, (long)(next - ev.Timestamp).TotalMilliseconds);

            CallStatus status = i == stepEvents.Count - 1
                ? (ended ? (exitCompleted ? CallStatus.Done : CallStatus.Failed) : CallStatus.Running)
                : CallStatus.Done;

            steps.Add((ProjectStep(ev, children, i, durMs, status, ended), ev));
        }

        return new RunSteps { Steps = steps, Ended = ended, ExitCompleted = exitCompleted, End = end };
    }

    private static StepView ProjectStep(RlogEvent stepEv, Dictionary<string, List<RlogEvent>> children, int ordinal, long durMs, CallStatus status, bool runEnded)
    {
        var kids = ChildrenOf(stepEv, children);
        string? thinking = JStr(kids.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.Thinking)?.Object1);
        string? message = JStr(kids.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.Content)?.Object1);

        var calls = new List<CallView>();
        foreach (var tc in kids.Where(e => e.Identifier == AgentReviLogger.Step.ToolCall))
            calls.Add(ProjectCall(tc, children, runEnded));
        // Synthesise the over-the-limit dropped calls (a single tool-dropped event lists them).
        foreach (var dropped in kids.Where(e => e.Identifier == AgentReviLogger.Step.ToolDropped))
            calls.AddRange(ProjectDropped(dropped));

        int? select = null;
        if (calls.Count > 5)
        {
            int idx = calls.FindIndex(c => c.Status == CallStatus.Running);
            if (idx < 0) idx = calls.FindIndex(c => c.Status == CallStatus.Failed);
            if (idx < 0) idx = calls.FindIndex(c => c.Status == CallStatus.Dropped);
            if (idx >= 0) select = idx + 1;
        }

        string state = Tag(stepEv, "agent-state") ?? "";
        string title = FirstLine(message) ?? (string.IsNullOrEmpty(state) ? $"Step {ordinal + 1}" : Capitalize(state));

        return new StepView
        {
            No = ordinal,
            Status = status,
            DurationMs = durMs > 0 ? durMs : null,
            Open = status == CallStatus.Running,
            Title = title,
            Thinking = thinking,
            Message = message,
            Calls = calls,
            Select = select,
        };
    }

    private static CallView ProjectCall(RlogEvent toolCall, Dictionary<string, List<RlogEvent>> children, bool runEnded)
    {
        var kids = ChildrenOf(toolCall, children);
        string name = JStr(toolCall.Object2) ?? "tool";
        string? input = JStr(toolCall.Object1);

        var subRoot = kids.FirstOrDefault(e => e.Identifier == "agent-run-start");
        if (subRoot is not null)
        {
            string subName = Tag(subRoot, "agent") ?? name;
            int? version = (int?)JObj(subRoot.Object2)?["version"];
            RunSteps sub = ProjectSteps(subRoot, children);
            CallStatus status = sub.Ended ? (sub.ExitCompleted ? CallStatus.Done : CallStatus.Failed) : (runEnded ? CallStatus.Failed : CallStatus.Running);
            string? finalOut = JStr(sub.End?.Object1);
            return new CallView
            {
                Kind = CallKind.SubAgent,
                Icon = "i-sub",
                Name = subName,
                Version = version.HasValue ? $"v{version}" : null,
                Status = status,
                Title = subName,
                Summary = Trunc(FirstLine(finalOut) ?? StatusWord(status), 90),
                Steps = sub.Steps.Select(s => s.View).ToList(),
            };
        }

        var result = kids.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.ToolResult);
        var start = kids.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.ToolStart);
        CallStatus toolStatus = result is not null
            ? (((bool?)JObj(result.Object2)?["failed"] ?? false) ? CallStatus.Failed : CallStatus.Done)
            : (runEnded ? CallStatus.Failed : CallStatus.Running);
        string? output = JStr(result?.Object1);
        long? durMs = result is not null ? Math.Max(0, (long)(result.Timestamp - (start ?? toolCall).Timestamp).TotalMilliseconds) : null;

        return new CallView
        {
            Kind = CallKind.Tool,
            Icon = IconFor(name),
            Name = name,
            Status = toolStatus,
            Title = name,
            Summary = Trunc(FirstLine(output) ?? FirstLine(input) ?? StatusWord(toolStatus), 90),
            DurationMs = durMs,
            Input = input,
            Output = output,
        };
    }

    private static IEnumerable<CallView> ProjectDropped(RlogEvent droppedEvent)
    {
        var arr = JArr(droppedEvent.Object1);
        if (arr is null) yield break;
        foreach (var item in arr)
        {
            string name = (string?)item["name"] ?? "tool";
            yield return new CallView
            {
                Kind = CallKind.Tool,
                Icon = IconFor(name),
                Name = name,
                Status = CallStatus.Dropped,
                Title = name,
                Summary = "dropped — over tool-call-limit",
                Input = (string?)item["input"],
                Output = "Dropped — the tool-call-limit was reached; this call was not executed.",
            };
        }
    }

    // ── header meta ──────────────────────────────────────────────────────────────────────────────

    private static InstanceMeta BuildMeta(RlogEvent root, RunSteps run, IReadOnlyList<RlogEvent> events, string sessionId, string? agentName)
    {
        var profile = JObj(root.Object2);
        string agent = agentName ?? Tag(root, "agent") ?? (string?)profile?["name"] ?? "(agent)";
        int? version = (int?)profile?["version"];

        // Model: prefer the model actually used (latest llm-response meta), else the entry state's model.
        string? model = events
            .Where(e => e.Identifier == AgentReviLogger.Step.LlmResponse)
            .Select(e => (string?)JObj(e.Object2)?["model"])
            .LastOrDefault(m => !string.IsNullOrEmpty(m));
        if (string.IsNullOrEmpty(model) && profile?["states"] is JArray states && states.Count > 0)
            model = (string?)states[0]?["model"];

        // Task: the run inputs' task/input value.
        var inputs = JObj(root.Object1);
        string task = (string?)inputs?["task"] ?? (string?)inputs?["input"] ?? "";

        int stepCount = run.Steps.Count;
        int toolCalls = events.Count(e => e.Identifier == AgentReviLogger.Step.ToolCall);
        int subAgents = events.Count(e => e.Identifier == "agent-run-start" && !ReferenceEquals(e, root));
        long tokens = events
            .Where(e => e.Identifier == AgentReviLogger.Step.LlmResponse)
            .Select(e => JObj(e.Object2))
            .Where(o => o is not null)
            .Sum(o => ((long?)o!["inputTokens"] ?? 0) + ((long?)o!["outputTokens"] ?? 0));

        CallStatus status = run.Ended ? (run.ExitCompleted ? CallStatus.Done : CallStatus.Failed) : CallStatus.Running;
        string statusText = run.Ended
            ? ((string?)JObj(run.End!.Object2)?["exitReason"] ?? "Completed")
            : $"Running · Step {Math.Max(1, stepCount)}";

        return new InstanceMeta
        {
            Agent = agent,
            Version = version.HasValue ? $"v{version}" : "—",
            Model = model ?? "—",
            Session = sessionId.Length > 12 ? sessionId[..12] : sessionId,
            Started = root.Timestamp.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.InvariantCulture),
            Status = status,
            StatusText = statusText,
            Task = task,
            Tiles =
            [
                new StatTile { Accent = "primary", Icon = "i-layers", Value = stepCount.ToString(CultureInfo.InvariantCulture), Label = "Steps" },
                new StatTile { Accent = "secondary", Icon = "i-tool", Value = toolCalls.ToString(CultureInfo.InvariantCulture), Label = "Tool calls" },
                new StatTile { Accent = "tertiary", Icon = "i-sub", Value = subAgents.ToString(CultureInfo.InvariantCulture), Label = "Sub-agents" },
                new StatTile { Accent = "info", Icon = "i-token", Value = FormatCount(tokens), Label = "Tokens" },
            ],
        };
    }

    // ── activation drafting (mutable while grouping, then materialised) ──────────────────────────

    private sealed class ActivationDraft
    {
        public required string State { get; init; }
        public required int Cycle { get; init; }
        public List<StepView> Steps { get; } = new();
        public string? EndSignal { get; set; }
        public string? NextState { get; set; }
    }

    private static CallStatus ActivationStatus(List<StepView> steps)
    {
        if (steps.Any(s => s.Status == CallStatus.Running)) return CallStatus.Running;
        if (steps.Any(s => s.Status == CallStatus.Failed)) return CallStatus.Failed;
        return CallStatus.Done;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static List<RlogEvent> ChildrenOf(RlogEvent e, Dictionary<string, List<RlogEvent>> children)
        => e.Id is { } id && children.TryGetValue(id, out var list) ? list : new List<RlogEvent>();

    private static DateTime MaxTimestamp(RlogEvent root, Dictionary<string, List<RlogEvent>> children)
    {
        DateTime max = root.Timestamp;
        foreach (var c in ChildrenOf(root, children))
        {
            var sub = MaxTimestamp(c, children);
            if (sub > max) max = sub;
        }
        return max;
    }

    private static string? Tag(RlogEvent e, string key)
    {
        if (string.IsNullOrEmpty(e.Tags)) return null;
        foreach (var token in e.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = token.IndexOf(':');
            if (colon > 0 && token.AsSpan(0, colon).SequenceEqual(key))
                return token[(colon + 1)..];
        }
        return null;
    }

    // Object1/Object2 are JSON (JsonConvert.SerializeObject). Parse a scalar string value.
    private static string? JStr(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var tok = JToken.Parse(json);
            return tok.Type == JTokenType.String ? tok.Value<string>() : tok.ToString();
        }
        catch { return json; }
    }

    private static JObject? JObj(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JToken.Parse(json) as JObject; }
        catch { return null; }
    }

    private static JArray? JArr(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JToken.Parse(json) as JArray; }
        catch { return null; }
    }

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var line = s.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? null : Trunc(line, 90);
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] + "…" : s;
    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string StatusWord(CallStatus s) => GroupedFormat.StatusLabel(s, null);

    private static string FormatCount(long n) =>
        n <= 0 ? "—" : n < 1000 ? n.ToString(CultureInfo.InvariantCulture)
        : n < 1_000_000 ? (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";

    private static string IconFor(string toolName) => toolName.ToLowerInvariant() switch
    {
        "web-scrape" => "i-globe",
        "web-search" => "i-search",
        "web-extract" => "i-code",
        "read-file" or "list-files" or "search-files" => "i-doc",
        "report" => "i-doc",
        "invoke_agent" => "i-sub",
        _ => "i-tool",
    };
}
