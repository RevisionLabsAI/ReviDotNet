// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Newtonsoft.Json;
using Revi;
using ReviDotNet.Forge.Services.Observer;
using ReviDotNet.Forge.Services.Workshop.Models;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// Default implementation of the Agent Workshop service. Drives multi-run agent execution,
/// captures live ReviLog events via the IWorkshopEventBus, and orchestrates evaluation
/// + revision via the AgentWorkshop.* prompts.
/// </summary>
public sealed class AgentWorkshopService : IAgentWorkshopService
{
    private const string EvaluatorPrompt = "AgentWorkshop.Evaluator";
    private const string ReviserPrompt = "AgentWorkshop.Reviser";

    private static readonly Regex SessionTagRegex =
        new(@"(?:^|\s)agent-session:([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IWorkshopEventBus _bus;
    private readonly IReviLogViewerService _logs;
    private readonly IInferService _infer;
    private readonly IAgentManager _agents;
    private readonly IModelManager _models;
    private readonly IPromptManager _prompts;
    private readonly IToolManager _tools;

    /// <summary>Initialises the workshop service with all required dependencies.</summary>
    public AgentWorkshopService(
        IWorkshopEventBus bus,
        IReviLogViewerService logs,
        IInferService infer,
        IAgentManager agents,
        IModelManager models,
        IPromptManager prompts,
        IToolManager tools)
    {
        _bus = bus;
        _logs = logs;
        _infer = infer;
        _agents = agents;
        _models = models;
        _prompts = prompts;
        _tools = tools;
    }

    // =====================================================
    //  Multi-run execution
    // =====================================================

    public async IAsyncEnumerable<WorkshopRunUpdate> RunMultiAsync(
        WorkshopRunRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AgentName))
            throw new ArgumentException("AgentName is required", nameof(request));

        AgentProfile profile = _agents.Get(request.AgentName)
            ?? throw new InvalidOperationException($"Agent '{request.AgentName}' not found.");

        int runs = Math.Max(1, request.Runs);
        var channel = Channel.CreateUnbounded<WorkshopRunUpdate>();

        // Kick off all runs in parallel.
        var runTasks = new List<Task>(runs);
        for (int i = 0; i < runs; i++)
        {
            int runIndex = i;
            runTasks.Add(Task.Run(() => RunOne(profile, request, runIndex, channel.Writer, ct), ct));
        }

        // When all runs finish, complete the channel.
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(runTasks); }
            finally { channel.Writer.Complete(); }
        }, ct);

        await foreach (var update in channel.Reader.ReadAllAsync(ct))
            yield return update;
    }

    private async Task RunOne(
        AgentProfile profile,
        WorkshopRunRequest request,
        int runIndex,
        ChannelWriter<WorkshopRunUpdate> writer,
        CancellationToken ct)
    {
        Dictionary<string, object> inputs = BuildInputs(request);

        AgentRunner runner = new(profile, inputs, ct, AgentRunContext.Root(),
            _models, _prompts, _tools);
        string sessionId = runner.SessionId;

        // Subscribe to live events for this session before starting the run.
        IDisposable subscription = _bus.Subscribe(sessionId, ev =>
        {
            // Best-effort fan-out; ignore if the writer is already closed.
            writer.TryWrite(new WorkshopRunUpdate
            {
                RunIndex = runIndex,
                SessionId = sessionId,
                Event = ev
            });
        });

        try
        {
            AgentResult result;
            try
            {
                result = await runner.RunAsync();
            }
            catch (Exception ex)
            {
                writer.TryWrite(new WorkshopRunUpdate
                {
                    RunIndex = runIndex,
                    SessionId = sessionId,
                    ErrorMessage = ex.Message
                });
                return;
            }

            writer.TryWrite(new WorkshopRunUpdate
            {
                RunIndex = runIndex,
                SessionId = sessionId,
                FinalResult = result
            });
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private static Dictionary<string, object> BuildInputs(WorkshopRunRequest req)
    {
        var inputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (req.AdditionalInputs is not null)
        {
            foreach (var kv in req.AdditionalInputs)
                inputs[kv.Key] = kv.Value;
        }

        if (!inputs.ContainsKey("input") && !string.IsNullOrEmpty(req.Task))
            inputs["input"] = req.Task;

        if (!inputs.ContainsKey("task") && !string.IsNullOrEmpty(req.Task))
            inputs["task"] = req.Task;

        return inputs;
    }


    // =====================================================
    //  Session lookups
    // =====================================================

    public Task<IReadOnlyList<RlogEvent>> GetSessionEventsAsync(string sessionId, CancellationToken ct)
        => _logs.GetSessionEventsAsync(sessionId, maxEvents: 5000, ct);

    public async Task<IReadOnlyList<AgentSessionSummary>> GetSessionsForAgentAsync(string agentName, int skip, int take, CancellationToken ct)
    {
        var sessions = await _logs.GetAgentSessionsAsync(agentName, skip, take, ct);

        var summaries = new List<AgentSessionSummary>(sessions.Count);
        foreach (var s in sessions)
        {
            // Pull the end event (if present) to get exit reason + final output preview.
            var events = await _logs.GetSessionEventsAsync(s.SessionId, maxEvents: 5000, ct);
            var endEvent = events.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.End);

            AgentExitReason? exitReason = null;
            string? preview = null;

            if (endEvent != null)
            {
                preview = endEvent.Object1;
                if (!string.IsNullOrEmpty(preview) && preview.Length > 200)
                    preview = preview[..200] + "…";

                if (endEvent.Object2 != null)
                {
                    try
                    {
                        var meta = JsonConvert.DeserializeObject<EndEventMeta>(endEvent.Object2);
                        if (meta != null && Enum.TryParse(meta.ExitReason, ignoreCase: true, out AgentExitReason parsed))
                            exitReason = parsed;
                    }
                    catch { /* ignore */ }
                }
            }

            summaries.Add(new AgentSessionSummary
            {
                SessionId = s.SessionId,
                AgentName = s.AgentName,
                StartedAt = s.StartedAt,
                LastSeenAt = s.LastSeenAt,
                EventCount = s.EventCount,
                ExitReason = exitReason,
                FinalOutputPreview = preview
            });
        }

        return summaries;
    }


    // =====================================================
    //  Evaluation
    // =====================================================

    public async Task<AgentEvaluationResult?> EvaluateSessionsAsync(string agentName, IReadOnlyList<string> sessionIds, CancellationToken ct)
    {
        if (sessionIds == null || sessionIds.Count == 0) return null;

        string? agentSource = await ReadAgentSourceAsync(agentName, ct);
        var runs = new List<EvaluatorRunPayload>();
        var stats = new RunningStats();

        foreach (var sid in sessionIds)
        {
            var events = await _logs.GetSessionEventsAsync(sid, maxEvents: 5000, ct);
            if (events.Count == 0) continue;

            var rootStart = events.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.Start);
            var endEvent = events.FirstOrDefault(e => e.Identifier == AgentReviLogger.Step.End);

            string finalOutput = endEvent?.Object1 ?? "";
            string exitReasonText = "Unknown";
            int totalSteps = 0;
            double durationSeconds = 0;

            if (endEvent?.Object2 != null)
            {
                try
                {
                    var meta = JsonConvert.DeserializeObject<EndEventMeta>(endEvent.Object2);
                    if (meta != null)
                    {
                        exitReasonText = meta.ExitReason ?? "Unknown";
                        totalSteps = meta.TotalSteps;
                    }
                }
                catch { /* ignore */ }
            }

            if (rootStart != null && endEvent != null)
                durationSeconds = (endEvent.Timestamp - rootStart.Timestamp).TotalSeconds;

            stats.Add(exitReasonText, durationSeconds, totalSteps);

            runs.Add(new EvaluatorRunPayload
            {
                SessionId = sid,
                ActivityLog = ProjectActivityLog(events),
                FinalOutput = finalOutput,
                ExitReason = exitReasonText,
                DurationSeconds = durationSeconds,
                TotalSteps = totalSteps
            });
        }

        if (runs.Count == 0) return null;

        var evalInputs = new List<Input>
        {
            new("Agent Name", agentName),
            new("Agent Definition", agentSource ?? "(.agent source not available on disk)"),
            new("Run Count", runs.Count.ToString()),
            new("Runs", JsonConvert.SerializeObject(runs, Formatting.Indented))
        };

        EvaluatorResponse? raw = await _infer.ToObject<EvaluatorResponse>(EvaluatorPrompt, evalInputs, token: ct);
        if (raw == null) return null;

        return new AgentEvaluationResult
        {
            Verdict = raw.Verdict ?? "unknown",
            Score = raw.Score,
            SuccessRate = stats.SuccessRate,
            Strengths = raw.Strengths ?? new List<string>(),
            Weaknesses = raw.Weaknesses ?? new List<string>(),
            Recommendations = (raw.Recommendations ?? new List<EvaluatorRec>())
                .Select(r => new AgentRecommendation
                {
                    Title = r.Title ?? "",
                    Rationale = r.Rationale ?? "",
                    Impact = r.Impact ?? "",
                    Kind = RecommendationKind.Recommendation
                }).ToList(),
            Alternatives = (raw.Alternatives ?? new List<EvaluatorRec>())
                .Select(r => new AgentRecommendation
                {
                    Title = r.Title ?? "",
                    Rationale = r.Rationale ?? "",
                    Impact = r.Impact ?? "",
                    Kind = RecommendationKind.Alternative
                }).ToList(),
            Stats = stats.ToAggregate()
        };
    }

    private static List<object> ProjectActivityLog(IReadOnlyList<RlogEvent> events)
    {
        // Compact projection so the evaluator prompt stays small but informative.
        return events.Select(e => (object)new
        {
            ts = e.Timestamp,
            step = e.Identifier,
            msg = e.Message,
            tags = e.Tags,
            object1 = TryTruncate(e.Object1, 4_000),
            object2 = TryTruncate(e.Object2, 1_000)
        }).ToList();
    }

    private static string? TryTruncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max] + "…";
    }


    // =====================================================
    //  Revision (diff generation + persistence)
    // =====================================================

    public async IAsyncEnumerable<string> GenerateAgentDiffAsync(
        string agentName,
        AgentRecommendation recommendation,
        AgentEvaluationResult evaluation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? current = await ReadAgentSourceAsync(agentName, ct);
        if (current == null)
            throw new InvalidOperationException($"Cannot generate revision: source .agent file for '{agentName}' not on disk.");

        Prompt? reviser = _prompts.Get(ReviserPrompt)
            ?? throw new InvalidOperationException($"Prompt '{ReviserPrompt}' not found.");

        var summary = new
        {
            verdict = evaluation.Verdict,
            score = evaluation.Score,
            successRate = evaluation.SuccessRate,
            strengths = evaluation.Strengths,
            weaknesses = evaluation.Weaknesses
        };

        var inputs = new List<Input>
        {
            new("Agent Name", agentName),
            new("Current Agent Content", current),
            new("Recommendation", JsonConvert.SerializeObject(new
            {
                title = recommendation.Title,
                rationale = recommendation.Rationale,
                impact = recommendation.Impact,
                kind = recommendation.Kind.ToString().ToLowerInvariant()
            }, Formatting.Indented)),
            new("Evaluation Summary", JsonConvert.SerializeObject(summary, Formatting.Indented))
        };

        await foreach (var token in _infer.CompletionStream(reviser, inputs).WithCancellation(ct))
            yield return token;
    }

    public async Task SaveAgentRevisionAsync(string agentName, string newContent, CancellationToken ct)
    {
        string? path = LocateAgentFile(agentName);
        if (path == null)
            throw new InvalidOperationException(
                $"Cannot save revision: source .agent file for '{agentName}' not on disk. " +
                "Embedded-resource agents are read-only.");

        await File.WriteAllTextAsync(path, newContent, ct);

        // Reload agent registry so the new revision is picked up.
        await _agents.LoadAsync(Assembly.GetExecutingAssembly());
    }

    public Task<string?> ReadAgentSourceAsync(string agentName, CancellationToken ct)
    {
        string? path = LocateAgentFile(agentName);
        if (path == null) return Task.FromResult<string?>(null);

        try { return File.ReadAllTextAsync(path, ct).ContinueWith(t => (string?)t.Result, ct); }
        catch { return Task.FromResult<string?>(null); }
    }

    private static string? LocateAgentFile(string agentName)
    {
        // .agent files live under RConfigs/Agents/<subdir>/<file>.agent, where the agent's
        // declared name is <subdir>/<basename>. We probe a few candidate paths.
        string root = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/Agents/";
        if (!Directory.Exists(root)) return null;

        // Try direct match: <root>/<name>.agent
        string direct = Path.Combine(root, agentName + ".agent");
        if (File.Exists(direct)) return direct;

        // Otherwise scan for any .agent file whose declared name matches.
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.agent", SearchOption.AllDirectories))
            {
                try
                {
                    var data = RConfigParser.Read(file);
                    if (data.TryGetValue("information_name", out var name) &&
                        string.Equals(name, agentName, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
                catch { /* skip */ }
            }
        }
        catch { /* ignore */ }

        return null;
    }


    // =====================================================
    //  Private DTOs (evaluator JSON contract)
    // =====================================================

    private sealed class EvaluatorRunPayload
    {
        [JsonProperty("session_id")] public string SessionId { get; set; } = "";
        [JsonProperty("activity_log")] public List<object> ActivityLog { get; set; } = new();
        [JsonProperty("final_output")] public string FinalOutput { get; set; } = "";
        [JsonProperty("exit_reason")] public string ExitReason { get; set; } = "";
        [JsonProperty("duration_seconds")] public double DurationSeconds { get; set; }
        [JsonProperty("total_steps")] public int TotalSteps { get; set; }
    }

    private sealed class EndEventMeta
    {
        [JsonProperty("exitReason")] public string? ExitReason { get; set; }
        [JsonProperty("totalSteps")] public int TotalSteps { get; set; }
        [JsonProperty("stateHistory")] public List<string>? StateHistory { get; set; }
        [JsonProperty("guardrailMessage")] public string? GuardrailMessage { get; set; }
    }

    private sealed class EvaluatorResponse
    {
        [JsonProperty("verdict")] public string? Verdict { get; set; }
        [JsonProperty("score")] public double Score { get; set; }
        [JsonProperty("strengths")] public List<string>? Strengths { get; set; }
        [JsonProperty("weaknesses")] public List<string>? Weaknesses { get; set; }
        [JsonProperty("recommendations")] public List<EvaluatorRec>? Recommendations { get; set; }
        [JsonProperty("alternatives")] public List<EvaluatorRec>? Alternatives { get; set; }
    }

    private sealed class EvaluatorRec
    {
        [JsonProperty("title")] public string? Title { get; set; }
        [JsonProperty("rationale")] public string? Rationale { get; set; }
        [JsonProperty("impact")] public string? Impact { get; set; }
    }

    private sealed class RunningStats
    {
        public int Total { get; private set; }
        public int Completed { get; private set; }
        public int Failed { get; private set; }
        public double TotalDurationSec { get; private set; }
        public int TotalSteps { get; private set; }

        public void Add(string exitReason, double durationSeconds, int steps)
        {
            Total++;
            if (string.Equals(exitReason, "Completed", StringComparison.OrdinalIgnoreCase))
                Completed++;
            else
                Failed++;
            TotalDurationSec += durationSeconds;
            TotalSteps += steps;
        }

        public double SuccessRate => Total == 0 ? 0 : (double)Completed / Total;

        public AggregateStats ToAggregate() => new()
        {
            TotalRuns = Total,
            Completed = Completed,
            Failed = Failed,
            SuccessRate = SuccessRate,
            AverageDurationSeconds = Total == 0 ? 0 : TotalDurationSec / Total,
            AverageSteps = Total == 0 ? 0 : (double)TotalSteps / Total
        };
    }
}
