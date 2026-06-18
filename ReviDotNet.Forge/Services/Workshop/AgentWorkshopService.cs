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
    private readonly ArtifactHistoryService? _history;

    /// <summary>
    /// In-memory revisions for agents that have no writable file on disk (embedded resources),
    /// keyed by agent name. Lets the workshop edit such agents for the lifetime of the process.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _inMemoryEdits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// In-memory capture of every run's ReviLog events, keyed by run-session id. Populated live as a
    /// run streams (see <see cref="RunOne"/>); read back by <see cref="ReadEventsAsync"/> so a completed
    /// session's trace still renders after navigation/reload and can be evaluated — even when no durable
    /// log store (Mongo) is configured. Bounded only by session activity for the process lifetime.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<RlogEvent>> _eventCache = new(StringComparer.Ordinal);

    /// <summary>Initialises the workshop service with all required dependencies.</summary>
    public AgentWorkshopService(
        IWorkshopEventBus bus,
        IReviLogViewerService logs,
        IInferService infer,
        IAgentManager agents,
        IModelManager models,
        IPromptManager prompts,
        IToolManager tools,
        ArtifactHistoryService? history = null)
    {
        _bus = bus;
        _logs = logs;
        _infer = infer;
        _agents = agents;
        _models = models;
        _prompts = prompts;
        _tools = tools;
        _history = history;
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

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkshopRunUpdate> RunChatTurnAsync(
        string agentName,
        IReadOnlyList<Message> seededHistory,
        IReadOnlyList<SessionAttachment>? attachments,
        CancellationToken ct)
        => RunMultiAsync(new WorkshopRunRequest
        {
            AgentName = agentName,
            Task = string.Empty,
            Runs = 1,
            Attachments = attachments,
            SeedHistory = seededHistory
        }, ct);

    private async Task RunOne(
        AgentProfile profile,
        WorkshopRunRequest request,
        int runIndex,
        ChannelWriter<WorkshopRunUpdate> writer,
        CancellationToken ct)
    {
        Dictionary<string, object> inputs = BuildInputs(request);

        // Attach the session's files (if any) to the run context so the file-access tools can reach
        // them; seed the conversation for a chat turn (otherwise the runner synthesises the first
        // user message from the inputs).
        SessionFileRegistry? files = BuildFileRegistry(request.Attachments);
        AgentRunner runner = new(profile, inputs, ct, AgentRunContext.Root(files),
            _models, _prompts, _tools, request.SeedHistory);
        string sessionId = runner.SessionId;

        // Subscribe to live events for this session before starting the run. Each event is both
        // streamed to the live view AND captured in _eventCache so the trace survives navigation/reload.
        var captured = _eventCache.GetOrAdd(sessionId, _ => new List<RlogEvent>());
        IDisposable subscription = _bus.Subscribe(sessionId, ev =>
        {
            lock (captured) captured.Add(ev);

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

    // Maps UI-side attachments to the Core file registry threaded through the run context.
    private static SessionFileRegistry? BuildFileRegistry(IReadOnlyList<SessionAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return null;

        var files = new List<SessionFile>(attachments.Count);
        for (int i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            files.Add(new SessionFile
            {
                Id = $"file-{i + 1}",
                Name = a.Name,
                MediaType = a.MediaType,
                Bytes = a.Bytes
            });
        }
        return new SessionFileRegistry(files);
    }


    // =====================================================
    //  Session lookups
    // =====================================================

    public Task<IReadOnlyList<RlogEvent>> GetSessionEventsAsync(string sessionId, CancellationToken ct)
        => ReadEventsAsync(sessionId, maxEvents: 5000, ct);

    /// <summary>
    /// Reads a run's events, preferring the durable log store and falling back to the in-memory
    /// <see cref="_eventCache"/> when no store is configured (or it has nothing for this session yet).
    /// This is what lets a completed run still render/evaluate locally with only the Null log store.
    /// </summary>
    private async Task<IReadOnlyList<RlogEvent>> ReadEventsAsync(string sessionId, int maxEvents, CancellationToken ct)
    {
        var persisted = await _logs.GetSessionEventsAsync(sessionId, maxEvents, ct);
        if (persisted.Count > 0) return persisted;

        if (_eventCache.TryGetValue(sessionId, out var cached))
        {
            lock (cached) return cached.ToList();
        }
        return persisted;
    }

    public async Task<IReadOnlyList<AgentSessionSummary>> GetSessionsForAgentAsync(string agentName, int skip, int take, CancellationToken ct)
    {
        var sessions = await _logs.GetAgentSessionsAsync(agentName, skip, take, ct);

        var summaries = new List<AgentSessionSummary>(sessions.Count);
        foreach (var s in sessions)
        {
            // Pull the end event (if present) to get exit reason + final output preview.
            var events = await ReadEventsAsync(s.SessionId, maxEvents: 5000, ct);
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
            var events = await ReadEventsAsync(sid, maxEvents: 5000, ct);
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
            new("Agent Definition", agentSource ?? "(.agent source unavailable)"),
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
            throw new InvalidOperationException($"Cannot generate revision: no source could be found for agent '{agentName}'.");

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
        if (path != null)
        {
            // Writable file on disk: snapshot the prior version, overwrite, and reload from disk.
            if (_history is not null)
            {
                try
                {
                    var existing = await File.ReadAllTextAsync(path, ct);
                    _history.Snapshot("agent", agentName, existing);
                }
                catch { /* best-effort */ }
            }

            await File.WriteAllTextAsync(path, newContent, ct);

            // Reload agent registry so the new revision is picked up.
            await _agents.LoadAsync(Assembly.GetExecutingAssembly(), ct);
            return;
        }

        // No writable file (embedded-resource agent): apply the edit in memory so the current
        // session's runs pick it up, without persisting to disk.
        SaveInMemoryRevision(agentName, newContent);
    }

    /// <summary>
    /// Applies an edit to an agent that has no writable file on disk. The prior text is snapshotted
    /// for version history, the new text is parsed and swapped into the live registry under the same
    /// name, and the edit is remembered so <see cref="ReadAgentSourceAsync"/> returns it.
    /// </summary>
    private void SaveInMemoryRevision(string agentName, string newContent)
    {
        if (_history is not null)
        {
            try
            {
                string? prior = _inMemoryEdits.TryGetValue(agentName, out var edited)
                    ? edited
                    : ReadEmbeddedAgentSource(agentName);
                if (prior is not null)
                    _history.Snapshot("agent", agentName, prior);
            }
            catch { /* best-effort */ }
        }

        // Re-parse the edited text and replace the live profile, keeping the agent's existing
        // registry identity (folder prefix included) so the workshop can still find it by name.
        var data = RConfigParser.ReadEmbedded(newContent);
        AgentProfile profile = AgentProfile.ToObject(data, namePrefix: "");
        profile.Name = agentName;
        _agents.AddOrReplace(profile);

        _inMemoryEdits[agentName] = newContent;
    }

    public Task<string?> ReadAgentSourceAsync(string agentName, CancellationToken ct)
    {
        // In-memory edits (for agents with no writable file on disk) take precedence.
        if (_inMemoryEdits.TryGetValue(agentName, out var edited))
            return Task.FromResult<string?>(edited);

        string? path = LocateAgentFile(agentName);
        if (path != null)
        {
            try { return File.ReadAllTextAsync(path, ct).ContinueWith(t => (string?)t.Result, ct); }
            catch { return Task.FromResult<string?>(null); }
        }

        // No file on disk: surface the source the agent was loaded from as an embedded resource.
        return Task.FromResult(ReadEmbeddedAgentSource(agentName));
    }

    public bool CanPersistToDisk(string agentName) => LocateAgentFile(agentName) != null;

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

    /// <summary>
    /// Reads the raw .agent text for an embedded-resource agent by matching the resource's declared
    /// name (folder prefix + information_name) against <paramref name="agentName"/>. Mirrors
    /// AgentManagerService.LoadFromEmbeddedResources so the text shown matches what was actually loaded.
    /// </summary>
    private static string? ReadEmbeddedAgentSource(string agentName)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        foreach (string resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Agents.") &&
                                 n.EndsWith(".agent", StringComparison.InvariantCultureIgnoreCase)))
        {
            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using StreamReader reader = new(stream);
                string content = reader.ReadToEnd();

                var data = RConfigParser.ReadEmbedded(content);
                if (!data.TryGetValue("information_name", out var baseName)) continue;

                string folder = Util.ExtractEmbeddedDirectories(".Agents.", resourceName).ToLower();
                if (string.Equals(folder + baseName, agentName, StringComparison.OrdinalIgnoreCase))
                    return content;
            }
            catch { /* skip unreadable / malformed resource */ }
        }

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
