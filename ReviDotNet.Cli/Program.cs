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
        "plugins" => await HandlePluginsAsync(rest, baseUrl, emitJson),
        "refine"  => await HandleRefineAsync(rest, baseUrl, emitJson),
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
    string? maxRoundsStr = GetFlag(args, "--max-rounds");
    string? mode         = GetFlag(args, "--mode") ?? "live";
    bool baselineOnly    = HasFlag(args, "--baseline-only");

    if (plugin is null) return Usage("--plugin is required for 'refine run'.");
    if (agent  is null) return Usage("--agent is required for 'refine run'.");
    if (suite  is null) return Usage("--suite is required for 'refine run'.");

    int samples   = samplesStr  is not null && int.TryParse(samplesStr,  out int s) ? s : 3;
    long? budget  = budgetStr   is not null && long.TryParse(budgetStr,  out long b) ? b : null;
    int maxRounds = maxRoundsStr is not null && int.TryParse(maxRoundsStr, out int m) ? m : 10;

    var spec = new CampaignSpec
    {
        PluginName         = plugin,
        AgentName          = agent,
        SuiteName          = suite,
        SamplesPerScenario = samples,
        TokenBudget        = budget,
        MaxRounds          = maxRounds,
        Mode               = mode,
        AutoPropose        = !baselineOnly,
    };

    // POST /campaigns
    if (emitJson)
    {
        // --json must emit exactly one JSON document on stdout: the final Campaign.
        (string jsonId, _) = await client.StartCampaignAsync(spec);
        Campaign final = await PollUntilTerminalAsync(jsonId, client, quiet: true);
        var finalJson = JsonSerializer.Serialize(final, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
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
// Polling
// ---------------------------------------------------------------------------
static async Task<Campaign> PollUntilTerminalAsync(string id, RefineryClient client, bool quiet)
{
    TimeSpan pollInterval = TimeSpan.FromSeconds(3);
    TimeSpan timeout = TimeSpan.FromMinutes(30);
    var deadline = DateTime.UtcNow + timeout;

    while (true)
    {
        Campaign c = await client.GetCampaignAsync(id);
        if (IsTerminal(c.Status)) return c;

        if (!quiet)
            Console.Write($"\r  status={c.Status,-20} tokens={c.TokensSpent,10}  rounds={c.Iterations.Count,3}  ");

        if (DateTime.UtcNow >= deadline)
        {
            if (!quiet) Console.WriteLine();
            Console.Error.WriteLine($"Polling timed out after {timeout.TotalMinutes:0} minutes. Last status: {c.Status}");
            return c;
        }

        await Task.Delay(pollInterval);
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
    Console.WriteLine($"Tokens    : {c.TokensSpent:N0}");
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
    Console.WriteLine($"    Quality mean  : {agg.QualityMean:F2}  p10={agg.QualityP10:F2}");
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
                --budget N        Token budget (long, default: no limit)
                --max-rounds N    Maximum improvement rounds (default 10)
                --mode            "live" or "replay" (default live)
                --baseline-only   Measure baseline only; skip proposal loop

          revi refine status <id>
              Print the current status and score summary for an existing campaign.

          revi refine list
              List all campaigns.

          revi refine ledger <id>
              Print a table of all ledger entries for a campaign (round, knob type,
              accepted/rejected, reject reason, held-out quality mean, invariant pass-rate,
              tokens spent).  --json prints the raw array.

        EXIT CODES
          0  Success
          1  Usage error
          2  HTTP or connection error
        """);
}
