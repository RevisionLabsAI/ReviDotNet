// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;

namespace Revi.Refinery;

/// <summary>
/// Projects the captured <see cref="RlogEvent"/>s of one run (already isolated per-run by the capture
/// broker, so it includes the root agent and any sub-agents it invoked) plus its <see cref="AgentResult"/>
/// into a typed <see cref="AgentTrace"/>. Tokens are summed across all the run's events (root + sub-agents);
/// the session id comes from the result, falling back to the parent-less run-root event.
/// </summary>
public static class AgentTraceBuilder
{
    /// <summary>Build a trace from a single run's captured events and result.</summary>
    public static AgentTrace Build(IReadOnlyList<RlogEvent> events, string agentName, AgentResult result)
    {
        List<RlogEvent> ordered = events.OrderBy(e => e.Timestamp).ToList();

        List<TraceEvent> traceEvents = new(ordered.Count);
        int inTok = 0, outTok = 0;
        foreach (RlogEvent e in ordered)
        {
            (_, string? state, int? depth) = ParseTags(e.Tags);
            traceEvents.Add(new TraceEvent
            {
                Type = e.Identifier ?? string.Empty,
                State = state,
                Cycle = e.Cycle,
                Depth = depth ?? 0,
                Object1 = e.Object1,
                Object2 = e.Object2,
                Timestamp = e.Timestamp
            });

            if (e.Identifier == TraceEventTypes.LlmResponse && !string.IsNullOrEmpty(e.Object2))
            {
                (int i, int o) = ParseTokens(e.Object2);
                inTok += i;
                outTok += o;
            }
        }

        string sessionId = !string.IsNullOrEmpty(result.SessionId)
            ? result.SessionId
            : ResolveRootSession(ordered);

        return new AgentTrace
        {
            SessionId = sessionId,
            AgentName = agentName,
            FinalOutput = result.FinalOutput,
            ExitReason = result.ExitReason.ToString(),
            TotalSteps = result.TotalSteps,
            InputTokens = inTok,
            OutputTokens = outTok,
            CostUsd = result.Cost,
            StateHistory = result.StateHistory ?? [],
            Events = traceEvents
        };
    }

    /// <summary>The run root has no parent; use its session, else the first session seen.</summary>
    private static string ResolveRootSession(IReadOnlyList<RlogEvent> ordered)
    {
        RlogEvent? root = ordered.FirstOrDefault(e => string.IsNullOrEmpty(e.ParentId));
        if (root is not null)
        {
            (string? s, _, _) = ParseTags(root.Tags);
            if (s is not null) return s;
        }
        foreach (RlogEvent e in ordered)
        {
            (string? s, _, _) = ParseTags(e.Tags);
            if (s is not null) return s;
        }
        return string.Empty;
    }

    /// <summary>Parse the agent tag block: <c>agent:X agent-session:Y agent-state:S agent-cycle:C agent-depth:D</c>.</summary>
    internal static (string? session, string? state, int? depth) ParseTags(string? tags)
    {
        if (string.IsNullOrEmpty(tags)) return (null, null, null);
        string? session = null, state = null;
        int? depth = null;
        foreach (string tok in tags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int c = tok.IndexOf(':');
            if (c <= 0) continue;
            string key = tok[..c];
            string val = tok[(c + 1)..];
            switch (key)
            {
                case "agent-session": session = val; break;
                case "agent-state": state = val; break;
                case "agent-depth": if (int.TryParse(val, out int d)) depth = d; break;
            }
        }
        return (session, state, depth);
    }

    private static (int input, int output) ParseTokens(string object2Json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(object2Json);
            int i = doc.RootElement.TryGetProperty("inputTokens", out JsonElement ip) && ip.TryGetInt32(out int iv) ? iv : 0;
            int o = doc.RootElement.TryGetProperty("outputTokens", out JsonElement op) && op.TryGetInt32(out int ov) ? ov : 0;
            return (i, o);
        }
        catch
        {
            return (0, 0);
        }
    }
}
