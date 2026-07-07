# Refinery — agent self-improvement toolkit

The Refinery is a reusable toolkit for **measuring and improving ReviDotNet agents**. A host (Forge) is
pointed at one or more local repos; it builds each repo's *refinement plugin*, loads it into an isolated
context, runs the plugin's agents against its scenarios, captures the resulting ReviLog traces, and scores
them with structural invariant checks, efficiency metrics, and an Opus-4.8 LLM judge. Over iterations it can
propose, gate, and accept prompt/agent changes — always behind a human-gated promotion step.

> **Status: shipped.** This document predates the implementation and describes the design. The toolkit
> has since landed in full — Phases 2b–6 plus waves 1–3: campaign execution, the `revi` CLI, campaign
> stop/promote, the optimize/test/calibration/scenario-generation surfaces, and the `/refinery` dashboard.
> For current usage docs see the Forge docs:
> [Refinery campaigns](ReviDotNet.Forge/Docs/refinery-campaigns.md),
> [plugin authoring](ReviDotNet.Forge/Docs/refinery-plugin-authoring.md),
> [the `revi` CLI](ReviDotNet.Forge/Docs/revi-cli.md), and the
> [Refinery section of the Forge feature reference](ReviDotNet.Forge/Docs/features.md).
> Where this file and those pages disagree, trust those pages.

## Assemblies

| Project | Role |
|---|---|
| `ReviDotNet.Refinery.Sdk` | The **plugin contract** (`IRefinementPlugin`) and all boundary DTOs (`Scenario`, `IInvariantChecker`, `AgentTrace`, `ScoreCard`, `Campaign`). A plugin references only this. |
| `ReviDotNet.Refinery` | The **engine**: per-run trace capture, structural / efficiency / LLM-judge scorers, aggregation, campaign store, runner, baseline controller, and `AddRefinery(...)`. Embeds the Evaluator judge prompts. |
| `ReviDotNet.Refinery.Hosting` | **Plugin lifecycle**: discover plugin projects → `dotnet build` → load into a collectible `AssemblyLoadContext` → instantiate the plugin → catalog / reload. |
| `ReviDotNet.Forge` | The **host**: wires the engine + hosting into DI, exposes the `/api/refinery` Control API and the `/refinery` dashboard. |

Dependency direction: `Sdk ← Refinery ← Hosting ← Forge`, and a plugin (e.g. `GreatDebate.Refinery`)
references `Sdk` only. `Core` sits under everything.

## The plugin contract

A consumer repo implements one class:

```csharp
public interface IRefinementPlugin
{
    string Name { get; }
    void ConfigureServices(IServiceCollection services, IConfiguration config); // repos → an ISOLATED test store
    IEnumerable<IBuiltInTool> CreateTools(IServiceProvider provider);           // real tools, bound to the test store
    IEnumerable<RefinableAgent> GetAgents();                                    // which agents to refine
    IEnumerable<ScenarioSuite> GetScenarioSuites();                             // inputs to run
    IEnumerable<IInvariantChecker> GetInvariantCheckers();                      // hard structural gates
}
```

The plugin's `.agent`/`.pmt` files are loaded into the host registry via `REVI_RCONFIG_PATHS` pointed at
the plugin repo's `RConfigs`.

## How Forge loads plugins

1. `Refinery:Repos` config lists local repo paths (optionally with a `.refinery.json` manifest).
2. `PluginDiscovery` finds the plugin `.csproj`; `PluginBuilder` runs `dotnet build` (incremental) and reads
   `TargetPath` via `msbuild -getProperty`.
3. `PluginLoader` loads the built assembly into a **collectible `PluginLoadContext`** that shares only
   `Core` / `Refinery.Sdk` / the DI+config+logging/hosting abstractions with the host — so
   `IRefinementPlugin` resolves to **one** type across contexts. Everything else is plugin-private and
   unloads on reload.
4. `PluginManager` exposes the catalog (`RefreshAllAsync` / `ReloadAsync` / `Catalog` / `Get`).

Because the host **builds and loads arbitrary local code**, only ever point it at **trusted local repos**.

## DI wiring (two things that are easy to get wrong)

```csharp
// AFTER the host registers its IRlogEventPublisher — AddRefinery DECORATES it:
builder.Services.AddRefinery();
builder.Services.AddRefineryHosting(builder.Configuration);

// so the engine's embedded judge prompts load into the registry:
builder.Services.AddReviDotNet(appAssembly, options =>
    options.AdditionalAssemblies.Add(typeof(RefineryServiceCollectionExtensions).Assembly));
```

- **Decorator ordering.** `AddRefinery` wraps the last-registered `IRlogEventPublisher` with
  `CompositeRlogPublisher`, which forwards to *both* the host's publisher (so Forge's live Observer/Workshop
  UI keeps working) *and* a `RefineryCaptureBroker`. Call it **after** the host's publisher is registered.
- **One shared broker.** Capture is per-async-context: the `AgentRunner` calls `broker.BeginCapture()`
  (`AsyncLocal`) and the composite calls into the **same** broker instance. When running a campaign from a
  per-plugin DI scope, the runner must use **the host-root broker** — the instance the composite feeds — or
  the capture comes back empty.

## Trace capture & scoring

- `RefineryCaptureBroker` + `AgentTraceBuilder` turn a run's ReviLog events into an `AgentTrace`
  (`SessionId`, token totals summed across sub-agents, `CostUsd`, typed `ToolCallsNamed`).
  `AgentResult.SessionId` / `AgentResult.Cost` (added in Core) make traces correctly identified and costed.
- A run is scored into a `ScoreCard`: **structural** invariant checks (hard gates → `Gated`), **efficiency**
  metrics (tokens / tool-calls / cost), and an **LLM judge** (`Evaluator.AgentRunJudge`, Opus-4.8 + thinking;
  `PairwiseJudge` for regression gating).
- `Aggregator` rolls runs into a `SuiteAggregate` with lower-bound stats; pass-rate is computed over
  **gated** runs only (`GatedRunCount`) so an empty suite can't show a false 100%.

## Control surface

- **Dashboard:** `/refinery` (MudBlazor) — plugin catalog with status, agents, suites, invariants; build &
  reload.
- **Control API:** `GET /api/refinery/plugins`, `POST /api/refinery/plugins/refresh`,
  `POST /api/refinery/plugins/{name}/reload`, `GET /api/refinery/campaigns[/{id}]`. (Campaign-start lands in
  Phase 3.) Currently **unauthenticated** — local/operator use only; add API-key auth before any non-local
  exposure.
- **CLI / MCP:** a `revi` CLI over the Control API is Phase 2b; an MCP server is deferred (TODO).

## Safety invariants

- Refinement runs against an **isolated test store**, never production data (the plugin's
  `ConfigureServices` is responsible for this).
- Promotion of a proposed change to real `.agent`/`.pmt` files is **human-gated**, never automatic.
- The plugin host builds + loads arbitrary local code → **trusted local repos only**.
