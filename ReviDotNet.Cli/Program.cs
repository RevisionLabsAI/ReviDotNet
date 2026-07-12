// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Revi.Refinery;
using ReviDotNet.Cli;

// ---------------------------------------------------------------------------
// Default base URL: http://localhost:5000 (Kestrel dev default; no
// launchSettings.json is present in the Forge project).
// ---------------------------------------------------------------------------
const string DefaultBaseUrl = "http://localhost:5000";

// ---------------------------------------------------------------------------
// Terminal campaign statuses
// ---------------------------------------------------------------------------
static bool IsTerminal(CampaignStatus s) =>
    s is CampaignStatus.Converged or CampaignStatus.Failed or CampaignStatus.Stopped or CampaignStatus.BudgetExhausted;

// ---------------------------------------------------------------------------
// Top-level try/catch — exit code 2 on connection/HTTP errors
// ---------------------------------------------------------------------------
try
{
    return await RunAsync(args);
}
catch (HttpRequestException ex)
{
    string url = Environment.GetEnvironmentVariable("FORGE_URL") ?? DefaultBaseUrl;
    Console.Error.WriteLine($"Connection error reaching {url}: {ex.Message}");
    Console.Error.WriteLine("Tip: make sure Forge is running and pass --url if using a non-default address.");
    return 2;
}
catch (RefineryHttpException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 2;
}

// ---------------------------------------------------------------------------
// Main dispatch
// ---------------------------------------------------------------------------
static async Task<int> RunAsync(string[] args)
{
    // ---- global flag extraction ----
    var (remainingArgs, baseUrl, emitJson) = ParseGlobalFlags(args);

    if (remainingArgs.Length == 0)
    {
        PrintHelp();
        return 0;
    }

    string verb = remainingArgs[0];
    string[] rest = remainingArgs[1..];

    return verb switch
    {
        "plugins"   => await HandlePluginsAsync(rest, baseUrl, emitJson),
        "refine"    => await HandleRefineAsync(rest, baseUrl, emitJson),
        "optimize"  => await HandleOptimizeAsync(rest, baseUrl, emitJson),
        "test"      => await HandleTestAsync(rest, baseUrl, emitJson),
        "calibrate" => await HandleCalibrateAsync(rest, baseUrl, emitJson),
        "generate"  => await HandleGenerateAsync(rest, baseUrl, emitJson),
        "--help" or "-h" or "help" => Help(),
        _ => Usage($"Unknown command '{verb}'.")
    };
}

// ---------------------------------------------------------------------------
// plugins sub-commands
// ---------------------------------------------------------------------------
static async Task<int> HandlePluginsAsync(string[] args, string baseUrl, bool emitJson)
{
    if (args.Length == 0) return Usage("Expected: plugins list | refresh | reload <name>");

    string sub = args[0];
    var client = new RefineryClient(baseUrl);

    switch (sub)
    {
        case "list":
        {
            using JsonDocument doc = await client.ListPluginsRawAsync();
            if (emitJson) { PrintJson(doc); return 0; }
            PrintPluginTable(doc.RootElement);
            return 0;
        }
        case "refresh":
        {
            Console.WriteLine("Refreshing all plugins…");
            using JsonDocument doc = await client.RefreshPluginsRawAsync();
            if (emitJson) { PrintJson(doc); return 0; }
            Console.WriteLine("Refresh complete.");
            PrintPluginTable(doc.RootElement);
            return 0;
        }
        case "reload":
        {
            if (args.Length < 2) return Usage("Expected: plugins reload <name>");
            string name = args[1];
            Console.WriteLine($"Reloading plugin '{name}'…");
            using JsonDocument doc = await client.ReloadPluginRawAsync(name);
            if (emitJson) { PrintJson(doc); return 0; }
            Console.WriteLine("Reload complete.");
            PrintPluginRow(doc.RootElement);
            return 0;
        }
        default:
            return Usage($"Unknown plugins sub-command '{sub}'.");
    }
}

// ---------------------------------------------------------------------------
// refine sub-commands
// ---------------------------------------------------------------------------
static async Task<int> HandleRefineAsync(string[] args, string baseUrl, bool emitJson)
{
    if (args.Length == 0) return Usage("Expected: refine run | status | list | ledger");

    string sub = args[0];
    var client = new RefineryClient(baseUrl);

    switch (sub)
    {
        case "run":
            return await HandleRefineRunAsync(args[1..], client, emitJson);
        case "status":
        {
            if (args.Length < 2) return Usage("Expected: refine status <id>");
            string id = args[1];
            if (emitJson)
            {
                using JsonDocument doc = await client.GetCampaignRawAsync(id);
                PrintJson(doc);
                return 0;
            }
            Campaign c = await client.GetCampaignAsync(id);
            PrintCampaignSummary(c);
            return 0;
        }
        case "list":
        {
            if (emitJson)
            {
                using JsonDocument doc = await client.ListCampaignsRawAsync();
                PrintJson(doc);
                return 0;
            }
            Campaign[] campaigns = await client.ListCampaignsAsync();
            PrintCampaignTable(campaigns);
            return 0;
        }
        case "ledger":
            return await HandleRefineLedgerAsync(args[1..], client, emitJson);
        case "stop":
        {
            if (args.Length < 2) return Usage("Expected: refine stop <id>");
            string id = args[1];
            await client.StopCampaignAsync(id); // throws RefineryHttpException on 404/400 (exit code 2)
            if (emitJson)
                Console.WriteLine("""{"stopped":true}""");
            else
                Console.WriteLine($"Stop requested for campaign {id} — it will land in Stopped shortly. Check with: revi refine status {id}");
            return 0;
        }
        default:
            return Usage($"Unknown refine sub-command '{sub}'.");
    }
}

static async Task<int> HandleRefineRunAsync(string[] args, RefineryClient client, bool emitJson)
{
    // Parse named flags
    string? plugin       = GetFlag(args, "--plugin");
    string? agent        = GetFlag(args, "--agent");
    string? suite        = GetFlag(args, "--suite");
    string? samplesStr   = GetFlag(args, "--samples");
    string? budgetStr    = GetFlag(args, "--budget");
    string? metaBudgetStr = GetFlag(args, "--meta-budget");
    string? maxRoundsStr = GetFlag(args, "--max-rounds");
    string? mode         = GetFlag(args, "--mode") ?? "live";
    string? parallelStr  = GetFlag(args, "--parallel");
    bool baselineOnly    = HasFlag(args, "--baseline-only");
    bool noScreen        = HasFlag(args, "--no-screen");

    if (plugin is null) return Usage("--plugin is required for 'refine run'.");
    if (agent  is null) return Usage("--agent is required for 'refine run'.");
    if (suite  is null) return Usage("--suite is required for 'refine run'.");

    int samples   = samplesStr  is not null && int.TryParse(samplesStr,  out int s) ? s : 3;
    long? budget  = budgetStr   is not null && long.TryParse(budgetStr,  out long b) ? b : null;
    long? metaBudget = metaBudgetStr is not null && long.TryParse(metaBudgetStr, out long mb) ? mb : null;
    int maxRounds = maxRoundsStr is not null && int.TryParse(maxRoundsStr, out int m) ? m : 10;
    int parallel  = parallelStr is not null && int.TryParse(parallelStr, out int p) ? p : 4;

    var spec = new CampaignSpec
    {
        PluginName         = plugin,
        AgentName          = agent,
        SuiteName          = suite,
        SamplesPerScenario = samples,
        TokenBudget        = budget,
        MetaTokenBudget    = metaBudget,
        MaxRounds          = maxRounds,
        Mode               = mode,
        AutoPropose        = !baselineOnly,
        MaxParallelRuns    = parallel,
        ScreenCandidates   = !noScreen,
    };

    // POST /campaigns
    if (emitJson)
    {
        // --json must emit exactly one JSON document on stdout: the final Campaign. Reuses the client's
        // wire-format options (single source of truth) with indentation layered on for readability.
        (string jsonId, _) = await client.StartCampaignAsync(spec);
        Campaign final = await PollUntilTerminalAsync(jsonId, client, quiet: true);
        var finalJson = JsonSerializer.Serialize(final, new JsonSerializerOptions(RefineryClient.JsonOpts)
        {
            WriteIndented = true,
        });
        Console.WriteLine(finalJson);
        return final.Status == CampaignStatus.Failed ? 2 : 0;
    }

    string modeLabel = baselineOnly ? "baseline-only" : "full refinement loop";
    (string id, string startStatus) = await client.StartCampaignAsync(spec);
    Console.WriteLine($"Campaign started  id={id}  status={startStatus}  mode={modeLabel}");
    Console.WriteLine($"Polling {client.BaseAddress} every 3 s (timeout 30 min)…");

    Campaign campaign = await PollUntilTerminalAsync(id, client, quiet: false);
    Console.WriteLine();
    PrintCampaignSummary(campaign);
    PrintLoopSummary(campaign);
    return campaign.Status == CampaignStatus.Failed ? 2 : 0;
}

static async Task<int> HandleRefineLedgerAsync(string[] args, RefineryClient client, bool emitJson)
{
    if (args.Length == 0) return Usage("Expected: refine ledger <id>");
    string id = args[0];

    if (emitJson)
    {
        using JsonDocument doc = await client.GetLedgerRawAsync(id);
        PrintJson(doc);
        return 0;
    }

    LedgerEntry[] entries = await client.GetLedgerAsync(id);
    PrintLedgerTable(entries);
    return 0;
}

// ---------------------------------------------------------------------------
// optimize
// ---------------------------------------------------------------------------
static async Task<int> HandleOptimizeAsync(string[] args, string baseUrl, bool emitJson)
{
    if (args.Length == 0) return Usage("Expected: optimize <promptName> [--models a,b] [--runs N] [--suggestions K] [--save <path>]");

    string promptName = args[0];
    string[] rest = args[1..];

    string? modelsStr      = GetFlag(rest, "--models");
    string? runsStr        = GetFlag(rest, "--runs");
    string? suggestionsStr = GetFlag(rest, "--suggestions");
    string? savePath       = GetFlag(rest, "--save");

    string[] modelNames = modelsStr is not null
        ? modelsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];

    int? runsPerModel   = runsStr        is not null && int.TryParse(runsStr,        out int r) ? r : null;
    int? maxSuggestions = suggestionsStr is not null && int.TryParse(suggestionsStr, out int k) ? k : null;

    var req = new OptimizeRequest(promptName, modelNames, [], runsPerModel, maxSuggestions);
    var client = new RefineryClient(baseUrl);

    if (emitJson)
    {
        using JsonDocument doc = await client.OptimizeRawAsync(req);
        PrintJson(doc);
        return 0;
    }

    OptimizeResponse result = await client.OptimizeAsync(req);

    Console.WriteLine($"Suggestions ({result.Suggestions.Count}):");
    for (int i = 0; i < result.Suggestions.Count; i++)
    {
        OptimizeSuggestion s = result.Suggestions[i];
        string section = string.IsNullOrWhiteSpace(s.AffectedSection) ? "" : $" [{s.AffectedSection}]";
        Console.WriteLine($"  [{i + 1}]{section} {s.Description}");
        if (!string.IsNullOrWhiteSpace(s.ExpectedImpact))
            Console.WriteLine($"       impact: {s.ExpectedImpact}");
    }

    Console.WriteLine();
    Console.WriteLine("Revised prompt:");
    Console.WriteLine(result.RevisedPromptContent);

    if (savePath is not null)
    {
        await File.WriteAllTextAsync(savePath, result.RevisedPromptContent);
        Console.WriteLine();
        Console.WriteLine($"Revised prompt saved to: {savePath}");
    }

    return 0;
}

// ---------------------------------------------------------------------------
// test
// ---------------------------------------------------------------------------
static async Task<int> HandleTestAsync(string[] args, string baseUrl, bool emitJson)
{
    if (args.Length == 0) return Usage("Expected: test <suiteName> [--agent <name>]");

    string suiteName = args[0];
    string[] rest = args[1..];
    string? agentName = GetFlag(rest, "--agent");

    var req = new TestRunRequest(suiteName, agentName);
    var client = new RefineryClient(baseUrl);

    if (emitJson)
    {
        using JsonDocument doc = await client.TestRunRawAsync(req);
        PrintJson(doc);
        // Still honour the CI contract: non-zero exit when any case failed.
        JsonElement root = doc.RootElement;
        int passed = root.TryGetProperty("passed", out JsonElement p) ? p.GetInt32() : 0;
        int total  = root.TryGetProperty("total",  out JsonElement t) ? t.GetInt32() : 0;
        return passed == total ? 0 : 1;
    }

    SuiteRunSummaryDto summary = await client.TestRunAsync(req);

    string modeLabel = string.IsNullOrWhiteSpace(summary.Mode) ? "prompt" : summary.Mode;
    Console.WriteLine($"Suite : {summary.SuiteName}  mode={modeLabel}");
    Console.WriteLine();
    Console.WriteLine($"  {"#",4}  {"PASS",4}  OUTPUT / FAILURES");
    Console.WriteLine($"  {new string('-', 60)}");
    foreach (SuiteCaseResultDto c in summary.Cases)
    {
        string passLabel = c.Passed ? "PASS" : "FAIL";
        string outputSnip = c.Output is not null
            ? (c.Output.Length > 60 ? c.Output[..57] + "..." : c.Output).ReplaceLineEndings(" ")
            : "";
        Console.WriteLine($"  {c.Index,4}  {passLabel,4}  {outputSnip}");
        if (c.Assertions is not null)
        {
            foreach (AssertionResultDto a in c.Assertions.Where(a => !a.Passed))
                Console.WriteLine($"              ASSERTION {a.Id} FAILED: {a.FailReason ?? "(no reason)"}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Result: {summary.Passed}/{summary.Total} passed");

    return summary.Passed == summary.Total ? 0 : 1;
}

// ---------------------------------------------------------------------------
// calibrate
// ---------------------------------------------------------------------------
static async Task<int> HandleCalibrateAsync(string[] args, string baseUrl, bool emitJson)
{
    string? agentName = GetFlag(args, "--agent");
    string? version   = GetFlag(args, "--version");

    if (agentName is null) return Usage("--agent is required for 'calibrate'.");

    var client = new RefineryClient(baseUrl);

    if (emitJson)
    {
        using JsonDocument doc = await client.GetCalibrationRawAsync(agentName, version);
        PrintJson(doc);
        return 0;
    }

    CalibrationReportDto report = await client.GetCalibrationAsync(agentName, version);

    string versionLabel = report.AgentVersion is not null ? $"  version={report.AgentVersion}" : "";
    Console.WriteLine($"Calibration report: {report.AgentName}{versionLabel}");
    Console.WriteLine($"  Runs (with truth): {report.TotalRuns}   Correct: {report.CalibratedRuns}");
    Console.WriteLine();

    if (report.Buckets is { Count: > 0 })
    {
        Console.WriteLine($"  {"CONFIDENCE",10}  {"RUNS",6}  {"CORRECT",7}  {"ACCURACY",8}  {"W-ERROR",9}");
        Console.WriteLine($"  {new string('-', 52)}");
        foreach (CalibrationBucketRow row in report.Buckets)
            Console.WriteLine($"  {row.ConfidenceLevel,10}  {row.RunCount,6}  {row.CorrectCount,7}  {row.Accuracy,8:P1}  {row.WeightedError,9:F4}");
        Console.WriteLine();
    }

    Console.WriteLine($"  ECE              : {report.Ece:F4}");
    Console.WriteLine($"  Monotonic        : {(report.MonotonicAccuracy ? "yes" : "no")}");

    return 0;
}

// ---------------------------------------------------------------------------
// generate
// ---------------------------------------------------------------------------
static async Task<int> HandleGenerateAsync(string[] args, string baseUrl, bool emitJson)
{
    string? agentName = GetFlag(args, "--agent");
    string? category  = GetFlag(args, "--category");
    string? countStr  = GetFlag(args, "--count");
    string? specFile  = GetFlag(args, "--spec");

    if (agentName is null) return Usage("--agent is required for 'generate'.");
    if (category  is null) return Usage("--category is required for 'generate'.");

    int? count = countStr is not null && int.TryParse(countStr, out int n) ? n : null;

    string agentSpecSection = "";
    if (specFile is not null)
    {
        if (!File.Exists(specFile))
            return Usage($"--spec file not found: {specFile}");
        agentSpecSection = await File.ReadAllTextAsync(specFile);
    }

    var req = new GenerateScenariosRequest(agentName, agentSpecSection, category, count);
    var client = new RefineryClient(baseUrl);

    if (emitJson)
    {
        using JsonDocument doc = await client.GenerateScenariosRawAsync(req);
        PrintJson(doc);
        return 0;
    }

    ScenarioDto[] scenarios = await client.GenerateScenariosAsync(req);

    Console.WriteLine($"Generated {scenarios.Length} scenario(s) for agent '{agentName}' / category '{category}':");
    Console.WriteLine();
    foreach (ScenarioDto s in scenarios)
    {
        string tags = s.Tags is { Length: > 0 } ? string.Join(", ", s.Tags) : "(none)";
        Console.WriteLine($"  id    : {s.Id}");
        Console.WriteLine($"  tags  : {tags}");
        if (!string.IsNullOrWhiteSpace(s.Notes))
            Console.WriteLine($"  notes : {s.Notes}");
        if (!string.IsNullOrWhiteSpace(s.GroundTruth))
            Console.WriteLine($"  truth : {s.GroundTruth}");
        Console.WriteLine();
    }

    return 0;
}

// ---------------------------------------------------------------------------
// Polling
// ---------------------------------------------------------------------------
static async Task<Campaign> PollUntilTerminalAsync(string id, RefineryClient client, bool quiet)
{
    TimeSpan pollInterval = TimeSpan.FromSeconds(3);
    TimeSpan timeout = TimeSpan.FromMinutes(30);
    var deadline = DateTime.UtcNow + timeout;

    // Ctrl-C interrupts the WATCH only — the campaign keeps running server-side ('revi refine stop <id>'
    // is what actually cancels it). e.Cancel = true keeps the process alive to print the parting message.
    using var watchCts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; watchCts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    Campaign? last = null;
    try
    {
        while (true)
        {
            Campaign c = last = await client.GetCampaignAsync(id, watchCts.Token);
            if (IsTerminal(c.Status)) return c;

            if (!quiet)
                Console.Write($"\r  status={c.Status,-20} agent-tokens={c.TokensSpent,10}  meta-tokens={c.MetaTokensSpent,10}  rounds={c.Iterations.Count,3}  ");

            if (DateTime.UtcNow >= deadline)
            {
                if (!quiet) Console.WriteLine();
                Console.Error.WriteLine($"Polling timed out after {timeout.TotalMinutes:0} minutes. Last status: {c.Status}");
                return c;
            }

            await Task.Delay(pollInterval, watchCts.Token);
        }
    }
    catch (OperationCanceledException) when (watchCts.IsCancellationRequested)
    {
        if (!quiet) Console.WriteLine();
        Console.Error.WriteLine(
            $"Watch interrupted — the campaign keeps running on the server. " +
            $"Check it with 'revi refine status {id}' or cancel it with 'revi refine stop {id}'.");
        return last ?? await client.GetCampaignAsync(id);
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

// ---------------------------------------------------------------------------
// Formatted output helpers
// ---------------------------------------------------------------------------
static void PrintPluginTable(JsonElement array)
{
    if (array.ValueKind != JsonValueKind.Array) { Console.WriteLine(array.ToString()); return; }
    Console.WriteLine($"{"NAME",-30} {"STATUS",-12} {"AGENTS",6} {"SUITES",6} {"INV",5}");
    Console.WriteLine(new string('-', 64));
    foreach (JsonElement p in array.EnumerateArray())
        PrintPluginRow(p);
}

static void PrintPluginRow(JsonElement p)
{
    string name    = p.TryGetProperty("name",   out JsonElement n) ? (n.GetString() ?? "") : "";
    string status  = p.TryGetProperty("status", out JsonElement st) ? (st.GetString() ?? "") : "";
    int agents = p.TryGetProperty("agents",     out JsonElement ag) && ag.ValueKind == JsonValueKind.Array ? ag.GetArrayLength() : 0;
    int suites = p.TryGetProperty("suites",     out JsonElement su) && su.ValueKind == JsonValueKind.Array ? su.GetArrayLength() : 0;
    int invs   = p.TryGetProperty("invariants", out JsonElement iv) && iv.ValueKind == JsonValueKind.Array ? iv.GetArrayLength() : 0;
    string? err  = p.TryGetProperty("error",   out JsonElement er) ? er.GetString() : null;
    string? warn = p.TryGetProperty("warning", out JsonElement wn) ? wn.GetString() : null;

    Console.WriteLine($"{name,-30} {status,-12} {agents,6} {suites,6} {invs,5}");
    if (!string.IsNullOrWhiteSpace(err))  Console.WriteLine($"  ERROR:   {err}");
    if (!string.IsNullOrWhiteSpace(warn)) Console.WriteLine($"  WARN:    {warn}");
}

static void PrintCampaignTable(Campaign[] campaigns)
{
    if (campaigns.Length == 0) { Console.WriteLine("(no campaigns)"); return; }
    Console.WriteLine($"{"ID",-38} {"STATUS",-16} {"PLUGIN",-20} {"AGENT",-20} {"TOKENS",10}");
    Console.WriteLine(new string('-', 108));
    foreach (Campaign c in campaigns)
        Console.WriteLine($"{c.Id,-38} {c.Status,-16} {c.Spec.PluginName,-20} {c.Spec.AgentName,-20} {c.TokensSpent,10}");
}

static void PrintCampaignSummary(Campaign c)
{
    Console.WriteLine($"Campaign  : {c.Id}");
    Console.WriteLine($"Status    : {c.Status}");
    Console.WriteLine($"Plugin    : {c.Spec.PluginName}");
    Console.WriteLine($"Agent     : {c.Spec.AgentName}");
    Console.WriteLine($"Suite     : {c.Spec.SuiteName}");
    Console.WriteLine($"Tokens    : {c.TokensSpent:N0} agent + {c.MetaTokensSpent:N0} meta (judge/gate/proposer)");
    Console.WriteLine($"Rounds    : {c.Iterations.Count}");
    if (c.Error is not null) Console.WriteLine($"Error     : {c.Error}");

    PrintAggregate("Baseline", c.Baseline);
    PrintAggregate("Current ", c.Current);
}

static void PrintLoopSummary(Campaign c)
{
    int totalVariants = c.Iterations.Sum(it => it.Variants.Count);
    int acceptedVariants = c.Iterations.Sum(it => it.Variants.Count(v => v.Accepted == true));

    Console.WriteLine();
    Console.WriteLine("--- Refinement loop summary ---");
    Console.WriteLine($"  Rounds run        : {c.Iterations.Count}");
    Console.WriteLine($"  Variants proposed : {totalVariants}");
    Console.WriteLine($"  Variants accepted : {acceptedVariants}");

    // Quality p10 delta
    double? baseP10   = c.Baseline?.QualityP10;
    double? currentP10 = c.Current?.QualityP10;
    if (baseP10 is not null && currentP10 is not null)
    {
        double delta = currentP10.Value - baseP10.Value;
        string sign  = delta >= 0 ? "+" : "";
        Console.WriteLine($"  Quality p10       : baseline={baseP10:F2}  final={currentP10:F2}  ({sign}{delta:F2})");
    }

    // Invariant pass-rate delta
    double? baseRate    = c.Baseline?.InvariantPassRate;
    double? currentRate = c.Current?.InvariantPassRate;
    if (baseRate is not null && currentRate is not null)
    {
        double delta = currentRate.Value - baseRate.Value;
        string sign  = delta >= 0 ? "+" : "";
        Console.WriteLine($"  Inv pass-rate     : baseline={baseRate:P1}  final={currentRate:P1}  ({sign}{delta:P1})");
    }
}

static void PrintLedgerTable(LedgerEntry[] entries)
{
    if (entries.Length == 0) { Console.WriteLine("(no ledger entries)"); return; }
    Console.WriteLine($"{"RND",4}  {"KNOB",-18}  {"ACC",3}  {"QUAL-MEAN",9}  {"INV-RATE",8}  {"TOKENS",10}  REJECT REASON");
    Console.WriteLine(new string('-', 90));
    foreach (LedgerEntry e in entries)
    {
        string acc     = e.Accepted ? "YES" : " no";
        string qual    = e.HeldOutScores?.QualityMean is double q ? $"{q,9:F2}" : $"{"—",9}";
        string invRate = e.HeldOutScores?.InvariantPassRate is double r ? $"{r,8:P1}" : $"{"—",8}";
        string reject  = e.RejectReason ?? "";
        // Truncate long reject reasons for table readability
        if (reject.Length > 50) reject = reject[..47] + "...";
        Console.WriteLine($"{e.Round,4}  {e.KnobType,-18}  {acc,3}  {qual}  {invRate}  {e.TokensSpent,10}  {reject}");
    }
}

static void PrintAggregate(string label, SuiteAggregate? agg)
{
    if (agg is null) { Console.WriteLine($"  {label}: (not yet available)"); return; }
    Console.WriteLine($"  {label}:");
    Console.WriteLine($"    Inv pass-rate : {agg.InvariantPassRate:P1}  (gated runs: {agg.GatedRunCount}/{agg.RunCount})");
    Console.WriteLine($"    Quality mean  : {agg.QualityMean:F2}  p10={agg.QualityP10:F2}  (judged: {agg.QualityScoredRuns}/{agg.RunCount}{(agg.QualityJudgeFailures > 0 ? $", JUDGE FAILURES: {agg.QualityJudgeFailures}" : "")})");
    Console.WriteLine($"    Cost mean     : ${agg.CostMean:F4}  latency p90={agg.LatencyP90Ms} ms");
    if (agg.InvariantPassRateById.Count > 0)
    {
        Console.WriteLine("    Per-invariant:");
        foreach (var (inv, rate) in agg.InvariantPassRateById)
            Console.WriteLine($"      {inv,-20} {rate:P1}");
    }
}

static void PrintJson(JsonDocument doc)
{
    Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
}

// ---------------------------------------------------------------------------
// Argument parsing helpers
// ---------------------------------------------------------------------------
static (string[] Remaining, string BaseUrl, bool EmitJson) ParseGlobalFlags(string[] args)
{
    var remaining = new List<string>();
    bool emitJson = false;
    string baseUrl = Environment.GetEnvironmentVariable("FORGE_URL") ?? DefaultBaseUrl;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--json")
        {
            emitJson = true;
        }
        else if (args[i] == "--url" && i + 1 < args.Length)
        {
            baseUrl = args[++i];
        }
        else
        {
            remaining.Add(args[i]);
        }
    }

    return ([.. remaining], baseUrl, emitJson);
}

/// <summary>Returns the value following <paramref name="flag"/> in <paramref name="args"/>, or null.</summary>
static string? GetFlag(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

/// <summary>Returns true if <paramref name="flag"/> appears anywhere in <paramref name="args"/>.</summary>
static bool HasFlag(string[] args, string flag)
{
    for (int i = 0; i < args.Length; i++)
        if (args[i] == flag) return true;
    return false;
}

// ---------------------------------------------------------------------------
// Help / usage
// ---------------------------------------------------------------------------
static int Help()
{
    PrintHelp();
    return 0;
}

static int Usage(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    Console.Error.WriteLine();
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        revi — Forge Refinery CLI

        GLOBAL FLAGS
          --url <baseUrl>   Forge base URL (env: FORGE_URL, default: http://localhost:5000)
          --json            Emit raw JSON instead of formatted text
          --help / -h       Show this help

        COMMANDS

          revi plugins list
              List all loaded plugins with their agents, suites, and invariant counts.

          revi plugins refresh
              Trigger a full plugin refresh and print the updated catalog.

          revi plugins reload <name>
              Hot-reload a single plugin by name.

          revi refine run --plugin <p> --agent <a> --suite <s>
                          [--samples N] [--budget N] [--max-rounds N] [--mode live|replay]
                          [--baseline-only]
              Start a refinement campaign, poll until terminal, then print a summary.
              By default the full refinement loop runs (AutoPropose=true): the engine
              proposes and evaluates variants each round until convergence or budget/rounds
              are exhausted.  Use --baseline-only to measure the baseline only without
              proposing any changes (AutoPropose=false).
              On completion, prints rounds run, variants proposed/accepted, and final vs
              baseline quality p10 and invariant pass-rate deltas.
              Options:
                --samples N       Samples per scenario (default 3)
                --budget N        Agent token budget (long, default: no limit)
                --meta-budget N   Meta-LLM token budget for judge/gate/proposer (long, default: no limit)
                --max-rounds N    Maximum improvement rounds (default 10)
                --parallel N      Max concurrent scenario runs (default 4; forced 1 for seeded suites)
                --no-screen       Disable the cheap 1-sample candidate screen before full evaluation
                --mode            "live" or "replay" (default live)
                --baseline-only   Measure baseline only; skip proposal loop

          revi refine status <id>
              Print the current status and score summary for an existing campaign.

          revi refine list
              List all campaigns.

          revi refine stop <id>
              Request cancellation of a queued or running campaign. The campaign lands
              in Stopped status shortly after. Errors with exit code 2 when the id is
              unknown or the campaign already finished.

          revi refine ledger <id>
              Print a table of all ledger entries for a campaign (round, knob type,
              accepted/rejected, reject reason, held-out quality mean, invariant pass-rate,
              tokens spent).  --json prints the raw array.

          revi optimize <promptName> [--models a,b,...] [--runs N] [--suggestions K]
                        [--save <path>]
              POST /api/refinery/optimize.  Runs the optimizer against one or more models
              and prints suggestion titles/descriptions plus the full revised prompt text.
              Use --save <path> to write the revised prompt to a file.
              --json prints the raw response.

          revi test <suiteName> [--agent <name>]
              POST /api/refinery/test/run.  Runs the named suite (in prompt-mode or
              agent-mode when --agent is given) and prints a per-case pass/fail table
              followed by an aggregate line.
              EXIT CODE 0 if all cases pass, 1 if any fail (CI-friendly).
              --json prints the SuiteRunSummary.

          revi calibrate --agent <name> [--version <v>]
              GET /api/refinery/calibration.  Prints the reliability table
              (confidence | runs | correct | accuracy | weighted-error), ECE, and
              whether the calibration curve is monotonic.
              --json prints the raw CalibrationReport.

          revi generate --agent <name> --category <cat> [--count N] [--spec <file>]
              POST /api/refinery/generate-scenarios.  Generates test scenarios for the
              given agent and category.  If --spec <file> is given its content is sent as
              the agentSpecSection; otherwise an empty string is used.
              Prints each scenario's id, tags, description, and notes.
              --json prints the raw Scenario array.

        EXIT CODES
          0  Success (or all test cases passed)
          1  Usage error (or test suite had failures)
          2  HTTP or connection error
        """);
}
