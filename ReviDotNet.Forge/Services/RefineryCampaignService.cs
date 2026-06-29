// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.DependencyInjection;
using Revi;
using Revi.Refinery;
using Revi.Refinery.Hosting;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Forge-side campaign orchestration: validates a <see cref="CampaignSpec"/> against a loaded plugin,
/// resolves the agent definition + scenario suite + invariant checkers, registers the plugin's tools into
/// the root <see cref="IToolManager"/>, then launches a background baseline run via
/// <see cref="RefinementController.MeasureBaselineAsync"/>. Clients (CLI / dashboard) poll the campaign by
/// the id this returns.
/// <para>
/// Runs are serialized with a <see cref="SemaphoreSlim"/> because <see cref="IToolManager"/> is not
/// thread-safe and tool register/unregister must never overlap a live run.
/// </para>
/// </summary>
public sealed class RefineryCampaignService(
    PluginManager plugins,
    RefinementController controller,
    IToolManager tools,
    ICampaignStore store,
    IConfiguration config,
    IAgentManager agentManager,
    ILogger<RefineryCampaignService> log)
{
    private readonly PluginManager _plugins = plugins;
    private readonly RefinementController _controller = controller;
    private readonly IToolManager _tools = tools;
    private readonly ICampaignStore _store = store;
    private readonly IConfiguration _config = config;
    private readonly IAgentManager _agentManager = agentManager;
    private readonly ILogger<RefineryCampaignService> _log = log;

    /// <summary>Serializes tool register/run/unregister cycles (IToolManager is not thread-safe).</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

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

        // (e) Launch the background run. NOTE: do not await — caller gets the id right away.
        _ = Task.Run(() => RunAsync(id, spec, suite, agentDefinition, checkers, lp, refine), CancellationToken.None);

        // (f)
        return id;
    }

    /// <summary>
    /// The background body: builds a per-plugin DI scope, registers its tools into the root tool manager,
    /// runs either the baseline measurement or the full campaign, then unregisters tools + disposes the scope
    /// in a finally. Marks the campaign Failed on any exception. Serialized by <see cref="_runGate"/>.
    /// </summary>
    private async Task RunAsync(
        string id,
        CampaignSpec spec,
        ScenarioSuite suite,
        string agentDefinition,
        IReadOnlyList<IInvariantChecker> checkers,
        LoadedPlugin lp,
        bool refine)
    {
        string kind = refine ? "campaign" : "baseline";
        await _runGate.WaitAsync();
        ServiceProvider? pluginProvider = null;
        List<IBuiltInTool> registered = [];
        try
        {
            // (c) Per-plugin DI scope + tool creation.
            ServiceCollection sc = new();
            lp.Plugin!.ConfigureServices(sc, _config);
            pluginProvider = sc.BuildServiceProvider();
            registered = lp.Plugin.CreateTools(pluginProvider).ToList();
            foreach (IBuiltInTool tool in registered)
                _tools.Register(tool);

            _log.LogInformation(
                "Refinery {Kind} {CampaignId} starting: plugin={Plugin} agent={Agent} suite={Suite} samples={Samples} rounds={Rounds} tools={Tools}",
                kind, id, spec.PluginName, spec.AgentName, spec.SuiteName, spec.SamplesPerScenario, spec.MaxRounds, registered.Count);

            if (refine)
            {
                await _controller.RunCampaignAsync(
                    spec, suite, agentDefinition, checkers, progress: null, ct: CancellationToken.None, campaignId: id);
            }
            else
            {
                await _controller.MeasureBaselineAsync(
                    spec, suite, agentDefinition, checkers, progress: null, ct: CancellationToken.None, campaignId: id);
            }

            _log.LogInformation("Refinery {Kind} {CampaignId} complete.", kind, id);
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
            foreach (IBuiltInTool tool in registered)
            {
                try { _tools.Unregister(tool.Name); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to unregister tool {Tool}.", tool.Name); }
            }
            if (pluginProvider is not null)
            {
                // Guard disposal: a misbehaving plugin service's Dispose must NOT prevent the gate release
                // below, or the process-wide _runGate would deadlock every future campaign.
                try { await pluginProvider.DisposeAsync(); }
                catch (Exception ex) { _log.LogWarning(ex, "Refinery {Kind} {CampaignId}: error disposing plugin scope.", kind, id); }
            }
            _runGate.Release();
        }
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
            ?? "GreatDebate.Researcher/RConfigs/Agents/{agent}.agent";
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
    /// agent definition from the stored <see cref="VariantRecord.Diff"/> (the SDK persists the diff, not the
    /// full content). Supports standard <c>@@ -l,s +l,s @@</c> hunks with space/'-'/'+' line prefixes. This is
    /// deliberately conservative: any structural surprise (no hunks, context/removed lines that do not match
    /// the source, hunk header past EOF) fails the whole apply so we never write a corrupt <c>.agent</c> file.
    /// </summary>
    private static class UnifiedDiff
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
