# Authoring a Refinery plugin

A Refinery plugin teaches the Refinery engine how to evaluate and improve *your* agents.
It is an ordinary C# class library that lives in your own local repo. Forge (the host)
discovers it, builds it with `dotnet build`, loads the output assembly into an isolated
load context, and asks it for four things: services + tools, agents, scenario suites,
and invariant checkers.

Sources: [Plugin.cs](../../ReviDotNet.Refinery.Sdk/Plugin.cs),
[Scenarios.cs](../../ReviDotNet.Refinery.Sdk/Scenarios.cs),
[Invariants.cs](../../ReviDotNet.Refinery.Sdk/Invariants.cs),
[PluginManager.cs](../../ReviDotNet.Refinery.Hosting/PluginManager.cs).

> **Security warning.** A plugin is arbitrary code executing inside the Forge process
> with Forge's permissions. The collectible `AssemblyLoadContext` is an *unload*
> mechanism, not a sandbox. Only add repos you control and trust to `Refinery:Repos` —
> never point it at a directory where untrusted code could appear.

---

## The contract: `IRefinementPlugin`

One class per plugin implements `IRefinementPlugin` (namespace `Revi.Refinery`). It must
have a public parameterless constructor — the loader instantiates it with
`Activator.CreateInstance`.

| Member | Responsibility |
| --- | --- |
| `Name` | Stable display/identifier name, e.g. `"GreatDebate"`. This is what campaigns, the CLI, and the dashboard refer to. |
| `ConfigureServices(services, configuration)` | Register the app's services into a **per-campaign DI scope** — repositories bound to an **isolated test store**, search/scrape configuration, etc. Never wire production data sinks here. |
| `CreateTools(services)` | Create the agent tools (e.g. domain bridge tools) resolved from that configured scope. Returns `IEnumerable<IBuiltInTool>`. |
| `GetAgents()` | The agents this plugin exposes for evaluation/refinement, as `RefinableAgent(Name, Description)` records. |
| `GetScenarioSuites()` | Scenario suites used to evaluate the agents. |
| `GetInvariantCheckers()` | Structural pass/fail gates evaluated against each run's trace. |

`RefinableAgent.Name` is the agent's **effective name in the host's ReviDotNet agent
registry**. The plugin does not ship the `.agent`/`.pmt` files through this interface —
the host must load them via its RConfig paths (see
[Wiring the plugin into Forge](#wiring-the-plugin-into-forge) below).

---

## Step-by-step: a minimal plugin

### 1. Create the project

Any class library works. The **only hard requirement** for discovery is an exact
`ProjectReference` or `PackageReference` to `ReviDotNet.Refinery.Sdk`
(`PluginDiscovery` parses the csproj as XML and compares the reference identity —
substring lookalikes such as `ReviDotNet.Refinery.Sdk.Extras` do not match):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\ReviDotNet\ReviDotNet.Refinery.Sdk\ReviDotNet.Refinery.Sdk.csproj" />
  </ItemGroup>
</Project>
```

Keep the plugin's `ReviDotNet.Core` / `ReviDotNet.Refinery.Sdk` versions in sync with
the host — the loader warns on version skew (`built against X, host runs Y`) because the
contract types must have a single identity across the load-context boundary.

### 2. Implement the plugin

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Revi;
using Revi.Refinery;

public sealed class MyAppRefinementPlugin : IRefinementPlugin
{
    public string Name => "MyApp";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Isolated test store — NEVER the production database.
        services.AddSingleton<IIssueRepository, InMemoryIssueRepository>();
    }

    public IEnumerable<IBuiltInTool> CreateTools(IServiceProvider services) =>
        [new IssueLookupTool(services.GetRequiredService<IIssueRepository>())];

    public IEnumerable<RefinableAgent> GetAgents() =>
        [new RefinableAgent("MyApp.Triage", "Triages incoming issues")];

    public IEnumerable<ScenarioSuite> GetScenarioSuites() =>
    [
        new ScenarioSuite
        {
            Name = "triage-core",
            AgentName = "MyApp.Triage",
            Scenarios =
            [
                new Scenario
                {
                    Id = "dup-detection-1",
                    AgentName = "MyApp.Triage",
                    Inputs = new Dictionary<string, string>
                        { ["issueContext"] = "Crash on save", ["userMessage"] = "App crashes when I hit save" },
                    WorldSeed = "ten-open-issues",
                    Rubric = ["Groundedness", "Completeness"],
                    ExpectedInvariants = ["T-1"],
                    GroundTruth = "duplicate-of-1042",
                    Notes = "Must find the existing crash-on-save issue."
                },
                new Scenario
                {
                    Id = "dup-detection-holdout-1",
                    AgentName = "MyApp.Triage",
                    Inputs = new Dictionary<string, string>
                        { ["issueContext"] = "Slow startup", ["userMessage"] = "Takes 30s to open" },
                    HeldOut = true,          // validation only — never shown to the proposer
                    Rubric = ["Groundedness", "Completeness"],
                    ExpectedInvariants = ["T-1"]
                }
            ]
        }
    ];

    public IEnumerable<IInvariantChecker> GetInvariantCheckers() =>
        [new SearchedBeforeFilingChecker()];
}
```

### 3. Ship the agent's RConfigs

The engine resolves `RefinableAgent.Name` against the **host's** agent registry, so the
plugin repo's `.agent`/`.pmt` files must be loaded into Forge. Point `REVI_RCONFIG_PATHS`
at the plugin repo's RConfigs root (see the wiring section). If the agent name is not in
the registry, campaign start fails with `agent '<name>' not found in plugin '<plugin>'`
validation aside, and the judge falls back to weaker agent-definition context.

---

## Designing scenario suites

A `ScenarioSuite` is a named set of `Scenario` records for one agent. Fields that matter:

- **`Id`** — stable and unique within the suite. Results, calibration, and reports key
  on it; do not rename ids casually.
- **`Inputs`** — the named inputs passed to the agent run (e.g. `issueContext`,
  `userMessage`).
- **`HeldOut`** — when `true`, the scenario is excluded from proposal generation and
  used **only for validation**. Split your suite: enough train scenarios for the
  proposer to learn from, plus a held-out set so an "improvement" that merely overfits
  the train scenarios gets caught. Held-out scenarios should cover the same behaviors
  as the train set, not new ones.
- **`Rubric`** — quality facet names the LLM judge scores (e.g. `Groundedness`,
  `Neutrality`). Keep facets few and orthogonal; a facet the judge can't evaluate from
  the trace is noise.
- **`ExpectedInvariants`** — ids of the invariants expected to hold for this scenario
  (a subset of the plugin's checkers). Lets a checker exist without applying everywhere.
- **`GroundTruth`** — the known-correct answer when one exists (e.g. the expected
  fact-checker winner). Used by calibration analysis to judge whether a run's
  determination was correct. Leave `null` when there is no objective answer.
- **`WorldSeed`** — an opaque string your own `IScenarioWorld.SeedAsync` interprets
  (see below). The engine never parses it.
- **`Tags` / `Notes`** — freeform grouping and human intent.

### Deterministic replay: `ReplayScript`

For scenarios that must run without a live model — CI, invariant-checker development,
reproducing a trace — set `ReplayScript` to a list of `ReplayTurn`s. When the campaign
spec runs in `replay` mode, the agent-under-test is driven against these scripted
outputs instead of a live inference provider. Each turn supplies:

- `Signal` — transition signal for the step (e.g. `DONE`, `CONTINUE`); `null` = none
- `Content` — the step's output text (final turn's content becomes the final output)
- `ToolCalls` — tool names to request, each emitted with an empty input object (enough
  to exercise the tool-dispatch path deterministically)
- `PromptTokens` / `CompletionTokens` — usage the turn reports, for cost fidelity

The replay seam consumes one turn per LLM call, in order, and repeats the final turn
once the script is exhausted. `ReplayScript = null` (the default) means live inference,
so existing suites are unaffected.

---

## The optional `IScenarioWorld` hook

If your scenarios need an isolated test store in a known state (seeded issues, fixture
documents, …), implement `IScenarioWorld` **on the same class** as `IRefinementPlugin`.
The engine detects it at runtime with an `is` check — plugins that need no store simply
don't implement it.

- `ResetAsync(pluginServices, ct)` — called **once before a run begins**. Clear or
  initialize the isolated test store.
- `SeedAsync(scenario, pluginServices, ct)` — called **immediately before every agent
  sample run** (each run may mutate the store). Receives the `Scenario` about to
  execute — typically you switch on `scenario.WorldSeed` — and the plugin's
  per-campaign DI scope.

Because `SeedAsync` runs before *every* sample, seeding must be idempotent and cheap.

---

## Writing invariant checkers

An `IInvariantChecker` is a deterministic, trace-checkable rule the agent **must**
satisfy — a hard gate: any failure fails the run regardless of judge quality scores.

```csharp
using Revi.Refinery;

public sealed class SearchedBeforeFilingChecker : IInvariantChecker
{
    public string Id => "T-1";
    public string Description => "Triage searched existing issues before filing a new one";
    public InvariantSeverity Severity => InvariantSeverity.High;

    public InvariantResult Check(AgentTrace trace, Scenario scenario)
    {
        bool searched = trace.ToolCallsFor("search_issues").Any();
        return searched
            ? InvariantResult.Pass(this, "search_issues was invoked")
            : InvariantResult.Fail(this, $"no search_issues call in {trace.TotalSteps} steps");
    }
}
```

Guidelines:

- Decide pass/fail **strictly from the trace and scenario** — no I/O, no LLM calls.
  `AgentTrace` gives you `FinalOutput`, `ExitReason`, `StateHistory`, the time-ordered
  `Events`, and helpers like `ToolCalls` / `ToolCallsFor(name)`.
- Give ids a stable short scheme (`T-1`, `CB-3`) — scenarios reference them via
  `ExpectedInvariants`.
- Pick `Severity` (`Low` / `Medium` / `High` / `Critical`) honestly; it drives gating
  weight and reporting.
- Always fill `Evidence` on failure (state/step/tool/output excerpt) — it is what shows
  up in reports and what the proposer reasons about.

---

## Wiring the plugin into Forge

Two pieces of configuration, both on the **Forge** side:

### 1. `Refinery:Repos` — where the plugin code lives

```jsonc
// appsettings.local.json
{
  "Refinery": {
    "Repos": [
      { "Path": "C:/Projects/MyApp" }
    ],
    "BuildOnStartup": true,
    "WatchForChanges": true
  }
}
```

Within each repo, project discovery runs in this order (first hit wins):

1. Explicit `Projects` list on the repo entry (paths relative to `Path`)
2. A `.refinery.json` manifest at the repo root: `{ "projects": ["src/MyApp.Refinery/MyApp.Refinery.csproj"], "buildConfiguration": "Release" }`
3. Convention: every `.csproj` in the repo (excluding `bin`/`obj`) with an exact
   reference to `ReviDotNet.Refinery.Sdk`

See [configuration.md](configuration.md) for the full option list
(`BuildConfiguration`, `TargetFramework`, per-repo overrides, `WatchDebounceMs`).

### 2. `REVI_RCONFIG_PATHS` — where the plugin's RConfigs live

Forge loads its own embedded RConfigs first; this env var adds extra on-disk RConfig
roots (each containing `Agents/`, `Prompts/`, `Tools/`, …) on top. Forge's own configs
always win on a name clash. Semicolon-separate multiple folders, or use numbered
variables:

```
REVI_RCONFIG_PATHS=C:/Projects/MyApp/MyApp.Agents/RConfigs
REVI_RCONFIG_PATHS_2=C:/Projects/OtherApp/RConfigs
```

This is what makes `RefinableAgent.Name` resolvable in the host registry — and it is
also the preferred source for the judge's agent-definition context: campaign start reads
the raw `.agent` file from the registry profile's `SourcePath`, falling back to the
profile's system prompt, then the `Refinery:AgentRConfigPath` path template under the
plugin's repo, then the bare agent name.

---

## Build, load, and hot reload

- **Startup** (`BuildOnStartup`, default `true`): Forge discovers all projects,
  `dotnet build`s each (incremental — a no-op when current), resolves the output
  assembly via MSBuild's `TargetPath`, and loads it into a **collectible
  `AssemblyLoadContext`**. `ReviDotNet.Core` and the Sdk are shared with the host so
  contract types have one identity.
- **Hot reload** (`WatchForChanges`, default `false`): when enabled, Forge watches each
  repo's `*.cs` / `*.csproj` files (ignoring `bin`/`obj`), debounces bursts
  (`WatchDebounceMs`, default 500 ms), and rebuilds + reloads the affected plugin.
- **The lease guarantee**: every campaign run pins its plugin with a lease for the
  duration of the run. Unload/reload **waits until all active leases are released**
  before tearing down the load context, so a file save mid-campaign can never yank the
  assembly out from under a running evaluation — the reload simply happens when the
  campaign finishes. A run that starts during the teardown window re-resolves the
  freshly reloaded plugin on its next attempt.
- **Manual control**: `revi plugins refresh` (rebuild + reload everything) and
  `revi plugins reload <name>` do the same on demand, via the Refinery API.

---

## Troubleshooting

Each plugin in the catalog carries a status: `Discovered` → `Building` → `Loaded`, or
`BuildFailed` / `LoadFailed` with an `Error` string and an optional `Warning`.

- **Where to look**: `revi plugins list` (name, status, agents, suites, invariant
  counts), the **Refinery dashboard** (`/refinery` in Forge), and the Forge log
  (`Refinery plugin build failed for …` / `… load failed for …` warnings).
- **`BuildFailed`** — the `Error` contains the tail of the `dotnet build` output. Build
  the project yourself from the repo to reproduce. Multi-targeted projects
  (`<TargetFrameworks>`) are built with `-p:TargetFramework=<tfm>` from
  `Refinery:TargetFramework` (default `net9.0`) — make sure the plugin actually targets
  that TFM.
- **`LoadFailed`** — common causes: no non-abstract `IRefinementPlugin` implementation
  in the assembly, no public parameterless constructor, or a type-load error from a
  dependency mismatch.
- **Version-skew warning** — the plugin was built against a different
  `ReviDotNet.Core` / `ReviDotNet.Refinery.Sdk` version than the host runs. It loads,
  but rebuild against the host's versions before trusting results.
- **`agent '<name>' not found`** on campaign start — `GetAgents()` doesn't list that
  name, or the agent's `.agent` file was never loaded into Forge's registry (check
  `REVI_RCONFIG_PATHS`).
- **Plugin not discovered at all** — verify the repo `Path` exists, and that the csproj
  has an *exact* `ProjectReference`/`PackageReference` to `ReviDotNet.Refinery.Sdk`
  (or list it explicitly via `Projects` / `.refinery.json`).
