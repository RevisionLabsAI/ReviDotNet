// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;

namespace Revi.Refinery;

/// <summary>
/// Projects captured <see cref="RlogEvent"/>s plus an <see cref="AgentResult"/> into a typed
/// <see cref="AgentTrace"/>. Reads ReviDotNet's agent tags
/// (<c>agent-session:</c>, <c>agent-state:</c>, <c>agent-depth:</c>) and the per-event identifier
/// (the step type, e.g. <c>tool-call</c>) to reconstruct the run.
/// </summary>
public static class AgentTraceBuilder
{
    /// <summary>Build a trace for the root (depth-0) agent run captured in <paramref name="events"/>.</summary>
    public static AgentTrace Build(IReadOnlyList<RlogEvent> events, string agentName, AgentResult result, string? sessionHint = null)
    {
        List<RlogEvent> ordered = events.OrderBy(e => e.Timestamp).ToList();

        // Determine the root session: the depth-0 start event, else the first session we see.
        string? root = sessionHint;
        if (string.IsNullOrEmpty(root))
        {
            foreach (RlogEvent e in ordered)
            {
                (string? sess, _, int? depth) = ParseTags(e.Tags);
                if (sess == null) continue;
                root ??= sess; // fallback: first session seen
                if (e.Identifier == TraceEventTypes.Start && depth is 0)
                {
                    root = sess;
                    break;
                }
            }
        }

        List<TraceEvent> traceEvents = [];
        int inTok = 0, outTok = 0;
        foreach (RlogEvent e in ordered)
        {
            (string? sess, string? state, int? depth) = ParseTags(e.Tags);
            if (root != null && sess != null && sess != root)
                continue; // restrict to the root run's events

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

        return new AgentTrace
        {
            SessionId = root ?? string.Empty,
            AgentName = agentName,
            FinalOutput = result.FinalOutput,
            ExitReason = result.ExitReason.ToString(),
            TotalSteps = result.TotalSteps,
            InputTokens = inTok,
            OutputTokens = outTok,
            StateHistory = result.StateHistory ?? [],
            Events = traceEvents
        };
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
