// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Revi;
using Revi.Refinery;
using Revi.Refinery.Hosting;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Forge-side campaign orchestration: validates a <see cref="CampaignSpec"/> against a loaded plugin,
/// resolves the agent definition + scenario suite + invariant checkers, builds a PER-RUN
/// <see cref="IToolManager"/> holding the plugin's tools (so the shared root manager is never mutated), then
/// launches a background baseline / campaign run via <see cref="RefinementController"/>, threading the
/// per-run tool manager through as the run's tool override. Clients (CLI / dashboard) poll the campaign by
/// the id this returns.
/// <para>
/// Per-run isolation (Wave3a): tools live in a fresh <see cref="ToolManagerService"/> per campaign and
/// candidates are run by profile (no shared <see cref="IAgentManager"/> slot), so a campaign no longer
/// mutates any process-wide registry. The substantive isolation mechanism (per-run tool override + candidate
/// runs by profile) is exercised concurrently by the controller-level tests.
/// </para>
/// <para>
/// <b>Run gate retained — correctness first.</b> Although the registry mutations that originally forced
/// serialization are gone, this Forge orchestrator has no dedicated test project in which to prove two FULL
/// service-level campaigns run concurrently without cross-talk (plugin DI scopes, <see cref="IScenarioWorld"/>
/// seeding/reset of a shared test store, and <see cref="PluginManager"/> leasing are not yet covered under
/// concurrency). Per the task's "relax only with a proving test, otherwise keep the gate" rule, the
/// <see cref="_runGate"/> is kept so campaigns remain serialized; it can be removed once a service-level
/// concurrency test exists.
/// </para>
/// </summary>
public sealed class RefineryCampaignService(
    PluginManager plugins,
    RefinementController controller,
    ICampaignStore store,
    IConfiguration config,
    IAgentManager agentManager,
    IServiceProvider rootServices,
    ILogger<RefineryCampaignService> log)
{
    private readonly PluginManager _plugins = plugins;
    private readonly RefinementController _controller = controller;
    private readonly ICampaignStore _store = store;
    private readonly IConfiguration _config = config;
    private readonly IAgentManager _agentManager = agentManager;
    private readonly IServiceProvider _rootServices = rootServices;
    private readonly ILogger<RefineryCampaignService> _log = log;

    /// <summary>
    /// Serializes campaign runs. The per-run tool isolation removed the IToolManager-mutation reason for this,
    /// but it is retained until a service-level concurrency test covers plugin-scope / IScenarioWorld /
    /// plugin-lease cross-talk (see the class remarks). Correctness over throughput.
    /// </summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>
    /// One <see cref="CancellationTokenSource"/> per in-flight (queued or running) campaign, keyed by
    /// campaign id. Registered in <see cref="StartAsync"/> BEFORE the background task launches and removed +
    /// disposed in the background body's finally, so <see cref="StopCampaign"/> can always find a live CTS
    /// for anything that has not reached a terminal state.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <summary>
    /// Per-campaign ring buffer of <see cref="RefineryProgress"/> messages — the live activity feed the
    /// dashboard renders. Kept after the campaign ends (session-lifetime) so a finished run's feed is still
    /// reviewable; capped per campaign so a long campaign cannot grow without bound.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _progress = new();

    private const int MaxProgressLines = 300;

    /// <summary>The campaign's captured progress lines, oldest first (empty for unknown ids).</summary>
    public IReadOnlyList<string> GetProgress(string id) =>
        _progress.TryGetValue(id, out ConcurrentQueue<string>? q) ? q.ToArray() : Array.Empty<string>();

    /// <summary>A progress sink that appends timestamped lines into the campaign's ring buffer.</summary>
    private IProgress<RefineryProgress> CreateProgressSink(string id)
    {
        ConcurrentQueue<string> q = _progress.GetOrAdd(id, _ => new ConcurrentQueue<string>());
        // Progress<T> posts via the captured SynchronizationContext; the background body runs on the thread
        // pool (none captured), so the handler runs inline on pool threads — ConcurrentQueue keeps it safe.
        return new Progress<RefineryProgress>(p =>
        {
            q.Enqueue($"{DateTime.Now:HH:mm:ss}  {p.Message}");
            while (q.Count > MaxProgressLines && q.TryDequeue(out _)) { }
            // Mirror into the host log so campaign progress survives the process (file log) — the ring
            // buffer above only serves the live UI and dies with the process.
            _log.LogInformation("campaign {CampaignId}: {Message}", id, p.Message);
        });
    }

    /// <summary>
    /// Validate the spec, pre-create a Pending campaign, and start a baseline-only measurement on a
    /// background task. Returns the campaign id immediately. Throws <see cref="ArgumentException"/> on
    /// validation failure (the API maps it to 400/404).
    /// </summary>
    public Task<string> StartBaselineAsync(CampaignSpec spec, CancellationToken ct = default)
        => StartAsync(spec, refine: false, ct);

    /// <summary>
    /// Validate the spec, pre-create a Pending campaign, and start a FULL refinement campaign (baseline +
    /// auto-proposed variants over rounds) on a background task. Returns the campaign id immediately. Throws
    /// <see cref="ArgumentException"/> on validation failure (the API maps it to 400/404).
    /// </summary>
    public Task<string> StartCampaignAsync(CampaignSpec spec, CancellationToken ct = default)
        => StartAsync(spec, refine: true, ct);

    /// <summary>
    /// Shared launcher for baseline and full-campaign runs: resolves + validates the plugin/suite/agent,
    /// pre-creates the Pending campaign, and kicks off the background body. <paramref name="refine"/> selects
    /// between <see cref="RefinementController.MeasureBaselineAsync"/> and
    /// <see cref="RefinementController.RunCampaignAsync"/>.
    /// </summary>
    private async Task<string> StartAsync(CampaignSpec spec, bool refine, CancellationToken ct)
    {
        // (a) Resolve + validate the plugin, suite, and agent.
        LoadedPlugin? lp = _plugins.Get(spec.PluginName);
        if (lp?.Plugin is null)
            throw new ArgumentException($"plugin '{spec.PluginName}' not found or not loaded");

        IRefinementPlugin plugin = lp.Plugin;

        ScenarioSuite? suite = plugin.GetScenarioSuites()
            .FirstOrDefault(s => string.Equals(s.Name, spec.SuiteName, StringComparison.OrdinalIgnoreCase));
        if (suite is null)
            throw new ArgumentException($"suite '{spec.SuiteName}' not found in plugin '{spec.PluginName}'");

        bool agentExists = plugin.GetAgents()
            .Any(a => string.Equals(a.Name, spec.AgentName, StringComparison.OrdinalIgnoreCase));
        if (!agentExists)
            throw new ArgumentException($"agent '{spec.AgentName}' not found in plugin '{spec.PluginName}'");

        // (b) Resolve the agent definition text and (one-time) the invariant checkers.
        string agentDefinition = ResolveAgentDefinition(spec.AgentName, lp);
        List<IInvariantChecker> checkers = plugin.GetInvariantCheckers().ToList();

        // (d) Pre-create the Pending campaign so clients can poll by id immediately.
        string id = Guid.NewGuid().ToString("n");
        Campaign initial = new() { Id = id, Spec = spec, Status = CampaignStatus.Pending };
        await _store.SaveAsync(initial, ct);

        // (e) Launch the background run. NOTE: do not await — caller gets the id right away. The per-campaign
        // CTS is registered BEFORE the task starts so a stop request can never miss the window; the background
        // body removes + disposes it in its finally.
        CancellationTokenSource runCts = new();
        _running[id] = runCts;
        _ = Task.Run(() => RunAsync(id, spec, suite, agentDefinition, checkers, lp, refine, runCts.Token), CancellationToken.None);

        // (f)
        return id;
    }

    /// <summary>
    /// Request cancellation of a queued or running campaign. Returns <c>true</c> when a live campaign was
    /// signalled (it will land in <see cref="CampaignStatus.Stopped"/> shortly), <c>false</c> when no
    /// in-flight campaign with that id exists (unknown id, or already terminal).
    /// </summary>
    public bool StopCampaign(string id)
    {
        if (_running.TryGetValue(id, out CancellationTokenSource? cts))
        {
            try
            {
                cts.Cancel();
                _log.LogInformation("Refinery campaign {CampaignId}: stop requested.", id);
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The run finished (and disposed its CTS) between the lookup and the Cancel — treat as
                // not-running; the campaign is already terminal.
            }
        }
        return false;
    }

    /// <summary>
    /// The background body: builds a per-plugin DI scope, creates a PER-RUN <see cref="IToolManager"/> holding
    /// the plugin's tools (the shared root manager is never touched), runs either the baseline measurement or
    /// the full campaign — passing that per-run tool manager as the run's tool override — then disposes the
    /// scope in a finally. Marks the campaign Failed on any exception and Stopped when
    /// <paramref name="ct"/> (the per-campaign stop token) fires. Serialized by <see cref="_runGate"/>
    /// (see the class remarks for why it is retained even though tool-mutation no longer requires it).
    /// </summary>
    private async Task RunAsync(
        string id,
        CampaignSpec spec,
        ScenarioSuite suite,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        LoadedPlugin lp,
        bool refine,
        CancellationToken ct)
    {
        string kind = refine ? "campaign" : "baseline";
        try
        {
            // A stop can arrive while this campaign is still QUEUED behind another; honour it here so a
            // stopped campaign never starts running.
            await _runGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await MarkStoppedAsync(id, spec, kind);
            RemoveRunCts(id);
            return;
        }
        ServiceProvider? pluginProvider = null;
        try
        {
            // (c) Per-plugin DI scope + tool creation.
            ServiceCollection sc = new();
            lp.Plugin!.ConfigureServices(sc, _config);
            pluginProvider = sc.BuildServiceProvider();

            // (c·1) Per-run tool registry: a fresh ToolManagerService (which registers the default built-ins)
            // into which we add ONLY this campaign's plugin tools. The root IToolManager is never mutated, so
            // two campaigns cannot see or stomp each other's tools.
            IToolManager runTools = CreatePerRunToolManager();
            List<IBuiltInTool> pluginTools = lp.Plugin.CreateTools(pluginProvider).ToList();
            foreach (IBuiltInTool tool in pluginTools)
                runTools.Register(tool);

            // Optional seeding hook: if the plugin manages an isolated test store, reset it once before the
            // run and seed it before every sample run via the callback. Plugins that do not implement
            // IScenarioWorld (e.g. the chatbot) pass a null callback — behaviour is unchanged.
            // Engage the world hook only when this SUITE actually seeds a world: a suite whose scenarios
            // carry no WorldSeed (e.g. the chatbot's inline-input scenarios) needs no isolated store, and
            // resetting one anyway couples it to infrastructure (Mongo) the run never uses — a store
            // outage would fail a campaign that had no store dependency at all.
            Func<Scenario, CancellationToken, Task>? seed = null;
            if (lp.Plugin is IScenarioWorld world && suite.Scenarios.Any(s => !string.IsNullOrEmpty(s.WorldSeed)))
            {
                await world.ResetAsync(pluginProvider, ct);
                seed = (s, c) => world.SeedAsync(s, pluginProvider, c);
            }

            _log.LogInformation(
                "Refinery {Kind} {CampaignId} starting: plugin={Plugin} agent={Agent} suite={Suite} samples={Samples} rounds={Rounds} tools={Tools}",
                kind, id, spec.PluginName, spec.AgentName, spec.SuiteName, spec.SamplesPerScenario, spec.MaxRounds, pluginTools.Count);

            // Pin the plugin for the duration of the run: a concurrent reload/unload must not tear down the
            // plugin's ALC (and the tools we created above) mid-campaign. PluginManager.Acquire returns a
            // lease that blocks teardown until disposed; it is a no-op disposable if the plugin is absent.
            using (_plugins.Acquire(spec.PluginName))
            {
                IProgress<RefineryProgress> progressSink = CreateProgressSink(id);
                if (refine)
                {
                    await _controller.RunCampaignAsync(
                        spec, suite, agentDefinition, checkers, progress: progressSink, ct: ct, campaignId: id, seedScenario: seed, toolManager: runTools);
                }
                else
                {
                    await _controller.MeasureBaselineAsync(
                        spec, suite, agentDefinition, checkers, progress: progressSink, ct: ct, campaignId: id, seedScenario: seed, toolManager: runTools);
                }
            }

            _log.LogInformation("Refinery {Kind} {CampaignId} complete.", kind, id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop requested. The controller already persists Stopped before rethrowing; MarkStoppedAsync is
            // a defensive no-op in that case and covers cancellation points OUTSIDE the controller (seeding,
            // plugin scope construction).
            await MarkStoppedAsync(id, spec, kind);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Refinery {Kind} {CampaignId} failed.", kind, id);
            try
            {
                Campaign? c = await _store.GetAsync(id);
                Campaign failed = (c ?? new Campaign { Id = id, Spec = spec }) with
                {
                    Status = CampaignStatus.Failed,
                    Error = ex.Message
                };
                await _store.SaveAsync(failed);
            }
            catch (Exception saveEx)
            {
                _log.LogError(saveEx, "Refinery {Kind} {CampaignId}: failed to persist failure state.", kind, id);
            }
        }
        finally
        {
            if (pluginProvider is not null)
            {
                // Guard disposal: a misbehaving plugin service's Dispose must NOT prevent the gate release
                // below, or the _runGate would deadlock every future campaign.
                try { await pluginProvider.DisposeAsync(); }
                catch (Exception ex) { _log.LogWarning(ex, "Refinery {Kind} {CampaignId}: error disposing plugin scope.", kind, id); }
            }
            _runGate.Release();
            RemoveRunCts(id);
        }
    }

    /// <summary>Remove and dispose the campaign's stop CTS once the run can no longer be cancelled.</summary>
    private void RemoveRunCts(string id)
    {
        if (_running.TryRemove(id, out CancellationTokenSource? cts))
            cts.Dispose();
    }

    /// <summary>
    /// Persist <see cref="CampaignStatus.Stopped"/> for a cancelled campaign unless the controller already
    /// moved it to a terminal state (it saves Stopped itself before rethrowing the cancellation).
    /// </summary>
    private async Task MarkStoppedAsync(string id, CampaignSpec spec, string kind)
    {
        try
        {
            Campaign? c = await _store.GetAsync(id);
            if (c is null || c.Status is CampaignStatus.Pending or CampaignStatus.Running)
            {
                Campaign stopped = (c ?? new Campaign { Id = id, Spec = spec }) with { Status = CampaignStatus.Stopped };
                await _store.SaveAsync(stopped);
            }
            _log.LogInformation("Refinery {Kind} {CampaignId} stopped.", kind, id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Refinery {Kind} {CampaignId}: failed to persist stopped state.", kind, id);
        }
    }

    /// <summary>
    /// Build a fresh per-run <see cref="ToolManagerService"/> (registers the default built-ins in its ctor),
    /// resolving its dependencies from the root service provider. The <see cref="Lazy{T}"/> wraps the SAME
    /// root <see cref="IAgentService"/> so the per-run manager's <c>invoke-agent</c> built-in dispatches
    /// through the real agent service; the plugin's own tools are layered on top by the caller. The root
    /// <see cref="IToolManager"/> registered in DI is never mutated.
    /// </summary>
    private IToolManager CreatePerRunToolManager()
    {
        Lazy<IAgentService> lazyAgents = new(() => _rootServices.GetRequiredService<IAgentService>());
        IWebContentService webContent = _rootServices.GetRequiredService<IWebContentService>();
        IModelManager models = _rootServices.GetRequiredService<IModelManager>();
        IReviLogger<ToolManagerService> logger = _rootServices.GetRequiredService<IReviLogger<ToolManagerService>>();
        return new ToolManagerService(lazyAgents, webContent, models, logger);
    }

    /// <summary>
    /// Human-gated promote: look up the campaign's accepted variant and apply its revised definition to the
    /// agent's on-disk <c>.agent</c> source, then swap the reparsed profile into the live registry (mirrors
    /// <c>AgentWorkshopService</c>'s write + <see cref="IAgentManager.AddOrReplace"/> pattern).
    /// <para>
    /// The accepted <see cref="VariantRecord"/> carries the full <see cref="VariantRecord.RevisedContent"/>,
    /// which is written directly. For older records that stored only a <see cref="VariantRecord.Diff"/>, the
    /// diff is applied to the current source as a fallback. If neither yields content (e.g. the agent has no
    /// writable file, or a legacy diff does not apply cleanly), nothing is written and <c>false</c> is returned
    /// (with a logged reason) rather than risk a corrupt file. Returns <c>true</c> only when the file was
    /// written and the registry updated.
    /// </para>
    /// </summary>
    public async Task<bool> PromoteVariantAsync(string campaignId, string variantId, CancellationToken ct = default)
    {
        Campaign? campaign = await _store.GetAsync(campaignId, ct);
        if (campaign is null)
        {
            _log.LogWarning("Promote: campaign {CampaignId} not found.", campaignId);
            return false;
        }

        // Locate the variant across all iterations; it must be marked accepted to be promotable.
        VariantRecord? variant = campaign.Iterations
            .SelectMany(it => it.Variants)
            .FirstOrDefault(v => v.Id == variantId);
        if (variant is null)
        {
            _log.LogWarning("Promote: variant {VariantId} not found in campaign {CampaignId}.", variantId, campaignId);
            return false;
        }
        if (variant.Accepted != true)
        {
            _log.LogWarning("Promote: variant {VariantId} is not accepted (Accepted={Accepted}); refusing to promote.",
                variantId, variant.Accepted);
            return false;
        }

        // Resolve the agent's writable source file.
        AgentProfile? profile = _agentManager.Get(variant.AgentName);
        string? sourcePath = profile?.SourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            _log.LogWarning(
                "Promote: agent '{Agent}' has no writable .agent source on disk (SourcePath='{Path}'); cannot promote.",
                variant.AgentName, sourcePath);
            return false;
        }

        // Prefer the variant's full revised content (persisted on accept). Fall back to applying the stored
        // unified diff to the current source for legacy records that carry only a diff.
        string revisedContent;
        if (!string.IsNullOrWhiteSpace(variant.RevisedContent))
        {
            revisedContent = variant.RevisedContent;
        }
        else
        {
            string currentContent;
            try { currentContent = await File.ReadAllTextAsync(sourcePath, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Promote: failed to read agent source at {Path}.", sourcePath);
                return false;
            }

            if (!UnifiedDiff.TryApply(currentContent, variant.Diff, out revisedContent))
            {
                _log.LogWarning(
                    "Promote: variant {VariantId} of campaign {CampaignId} has no revised content and its stored " +
                    "diff did not apply cleanly to '{Path}' (empty/malformed diff or drifted source).",
                    variantId, campaignId, sourcePath);
                return false;
            }
        }

        try
        {
            await File.WriteAllTextAsync(sourcePath, revisedContent, ct);

            // Re-parse just this agent and swap it into the registry, keeping its name + source path
            // (mirrors AgentWorkshopService): do NOT reload the whole registry.
            Dictionary<string, string> data = RConfigParser.ReadEmbedded(revisedContent);
            AgentProfile updated = AgentProfile.ToObject(data, namePrefix: "");
            updated.Name = variant.AgentName;
            updated.SourcePath = sourcePath;
            _agentManager.AddOrReplace(updated);

            _log.LogInformation(
                "Promote: applied accepted variant {VariantId} (knob={Knob}) of campaign {CampaignId} to '{Path}'.",
                variantId, variant.KnobType, campaignId, sourcePath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Promote: failed to write/parse revised agent at {Path}.", sourcePath);
            return false;
        }
    }

    /// <summary>
    /// Resolves the agent definition TEXT used as judge context. Prefers the raw <c>.agent</c> source from
    /// the ReviDotNet registry Forge already runs agents from (<see cref="IAgentManager"/>): the loaded
    /// <see cref="AgentProfile.SourcePath"/> is the original file on disk. Falls back, in order, to the
    /// profile's <see cref="AgentProfile.SystemPrompt"/>, then a config-driven path template
    /// (<c>Refinery:AgentRConfigPath</c>) under the plugin's repo, then the bare agent name.
    /// </summary>
    private string ResolveAgentDefinition(string agentName, LoadedPlugin lp)
    {
        // 1. Registry: raw source file the profile was loaded from.
        AgentProfile? profile = _agentManager.Get(agentName);
        if (profile is not null)
        {
            if (!string.IsNullOrWhiteSpace(profile.SourcePath) && File.Exists(profile.SourcePath))
            {
                try { return File.ReadAllText(profile.SourcePath); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not read agent source at {Path}.", profile.SourcePath); }
            }
            if (!string.IsNullOrWhiteSpace(profile.SystemPrompt))
                return profile.SystemPrompt;
        }

        // 2. Config-driven path template relative to the plugin's repo root.
        string template = _config["Refinery:AgentRConfigPath"]
            ?? "RConfigs/Agents/{agent}.agent";
        string relative = template.Replace("{agent}", agentName);
        string diskPath = Path.IsPathRooted(relative) ? relative : Path.Combine(lp.RepoPath, relative);
        if (File.Exists(diskPath))
        {
            try { return File.ReadAllText(diskPath); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not read agent file at {Path}.", diskPath); }
        }

        // 3. Last resort: just the name (judge context degrades gracefully).
        _log.LogWarning(
            "No agent definition source found for '{Agent}' (registry SourcePath/SystemPrompt empty, '{Disk}' missing). Using the bare name as the definition.",
            agentName, diskPath);
        return agentName;
    }

    /// <summary>
    /// Minimal unified-diff applier used by <see cref="PromoteVariantAsync"/> to reconstruct the full revised
    /// agent definition from the stored <see cref="VariantRecord.Diff"/> when a record lacks
    /// <see cref="VariantRecord.RevisedContent"/>. Supports standard <c>@@ -l,s +l,s @@</c> hunks with
    /// space/'-'/'+' line prefixes — the exact format <c>LlmDiffProposer.UnifiedDiff</c> emits (one
    /// full-context hunk), so every stored diff is re-appliable. This is deliberately conservative: any
    /// structural surprise (no hunks, context/removed lines that do not match the source, hunk header past
    /// EOF) fails the whole apply so we never write a corrupt <c>.agent</c> file. Internal (not private)
    /// only so the round-trip contract with the proposer's diff format stays under test.
    /// </summary>
    internal static class UnifiedDiff
    {
        public static bool TryApply(string source, string diff, out string result)
        {
            result = source;
            if (string.IsNullOrWhiteSpace(diff))
                return false;

            // Normalize to LF for processing; preserve nothing fancy — agent files are small text.
            List<string> src = [.. source.Replace("\r\n", "\n").Split('\n')];
            string[] diffLines = diff.Replace("\r\n", "\n").Split('\n');

            List<string> output = [];
            int srcPos = 0;       // 0-based index into src already copied to output
            bool sawHunk = false;
            int i = 0;

            while (i < diffLines.Length)
            {
                string line = diffLines[i];

                // Skip file headers ("--- a/...", "+++ b/...") and any preamble before the first hunk.
                if (line.StartsWith("--- ", StringComparison.Ordinal)
                    || line.StartsWith("+++ ", StringComparison.Ordinal)
                    || line.StartsWith("diff ", StringComparison.Ordinal)
                    || line.StartsWith("index ", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (!TryParseHunkHeader(line, out int oldStart))
                        return false;

                    // Hunk old-start is 1-based; convert to 0-based. A header for an empty file uses 0.
                    int targetIndex = oldStart > 0 ? oldStart - 1 : 0;
                    if (targetIndex > src.Count)
                        return false;

                    // Copy untouched context between the last hunk and this one.
                    if (targetIndex < srcPos)
                        return false; // overlapping/out-of-order hunks: bail.
                    while (srcPos < targetIndex)
                        output.Add(src[srcPos++]);

                    sawHunk = true;
                    i++;

                    // Apply hunk body until the next hunk header / end of diff.
                    while (i < diffLines.Length && !diffLines[i].StartsWith("@@", StringComparison.Ordinal))
                    {
                        string body = diffLines[i];

                        // A trailing empty split element at EOF is not a real diff line.
                        if (body.Length == 0)
                        {
                            i++;
                            continue;
                        }

                        // Git "\ No newline at end of file" marker: informational only.
                        if (body.StartsWith("\\", StringComparison.Ordinal))
                        {
                            i++;
                            continue;
                        }

                        char tag = body[0];
                        string content = body[1..];

                        switch (tag)
                        {
                            case ' ': // context: must match source
                                if (srcPos >= src.Count || src[srcPos] != content)
                                    return false;
                                output.Add(src[srcPos++]);
                                break;
                            case '-': // removal: must match source, drop it
                                if (srcPos >= src.Count || src[srcPos] != content)
                                    return false;
                                srcPos++;
                                break;
                            case '+': // addition: emit, do not advance source
                                output.Add(content);
                                break;
                            default:
                                return false; // unknown prefix
                        }
                        i++;
                    }
                    continue;
                }

                // Non-empty, non-header, non-hunk content before any hunk is unexpected.
                if (!sawHunk && line.Length == 0)
                {
                    i++;
                    continue;
                }
                if (line.Length == 0)
                {
                    i++;
                    continue;
                }
                return false;
            }

            if (!sawHunk)
                return false;

            // Copy any remaining unchanged tail.
            while (srcPos < src.Count)
                output.Add(src[srcPos++]);

            result = string.Join("\n", output);
            return true;
        }

        /// <summary>Parses the old-file start line from a <c>@@ -l,s +l,s @@</c> header.</summary>
        private static bool TryParseHunkHeader(string header, out int oldStart)
        {
            oldStart = 0;
            // Expected form: @@ -<oldStart>[,<oldCount>] +<newStart>[,<newCount>] @@ [section]
            int minus = header.IndexOf('-');
            if (minus < 0) return false;
            int end = minus + 1;
            while (end < header.Length && (char.IsDigit(header[end])))
                end++;
            if (end == minus + 1) return false;
            return int.TryParse(header.AsSpan(minus + 1, end - (minus + 1)), out oldStart);
        }
    }
}
