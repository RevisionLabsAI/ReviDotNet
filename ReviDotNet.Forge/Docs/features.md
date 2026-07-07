# Feature reference

Every page and endpoint, in one place. Pages are listed in nav order.

## Navigation

The main nav drawer (`Components/Layout/MainLayout.razor`) groups features:

- **Dashboard** (`/`) — landing card grid
- **Studio** — Prompt Registry, Generate Prompt, Test Runner, Optimizer, Agent Workshop
- **Refinery** (`/refinery`) — agent refinement campaigns (standalone nav link)
- **Operations** — Observer, Usage, API Keys

There is also a dark-mode toggle in the AppBar. The drawer state and color scheme are
session-local; nothing is persisted.

---

## Dashboard (`/`)

Source: [Home.razor](../Components/Pages/Home.razor)

Four stat cards (loaded prompts, loaded models, two placeholder slots) plus a Quick Start
section linking out to Registry, Generate, and Test/Optimize. Counts come from
`PromptRegistryService.GetAll().Count` and `IModelManager.GetAll().Count` at page init.

This is intentionally light — it exists so the empty state of a fresh install tells the
user where to go first.

---

## Prompt Registry (`/prompts`)

Source: [Prompts.razor](../Components/Pages/Prompts/Prompts.razor) and
[PromptDetailDrawer.razor](../Components/Pages/Prompts/PromptDetailDrawer.razor)

**What it shows.** A data grid of every `Prompt` loaded by `IPromptManager`. Columns:
Name, Version, Schema (chip showing `json-auto` / `json` / `regex` / `gbnf` / `JSON` /
`None`), Examples count, Actions.

**Actions per row:**

- **View** — opens a right-side `PromptDetailDrawer` with system, instruction, schema,
  tuning settings, and the first five examples in expansion panels.
- **Edit** — opens an edit drawer with editable system, instruction, schema fields.
  Saves via `PromptRegistryService.Save(prompt)`, which serializes the `Prompt` back to
  `.pmt` format (preserving `[[information]]`, `[[settings]]`, `[[tuning]]`,
  `[[_system]]`, `[[_instruction]]`, `[[_schema]]` sections) and writes it under
  `Forge:PromptsSourcePath` (`RConfigs/Prompts` by default), then reloads it into the
  in-memory registry.
- **Test** — navigates to `/test?prompt=<name>`.
- **Optimize** — navigates to `/optimize?prompt=<name>`.

**Filtering.** A search box does case-insensitive contains-matching on the prompt name.

**Caveat.** The edit drawer only surfaces three fields. Tuning parameters (temperature,
top-k, etc.) and settings (request-json, guidance-schema-type, etc.) can only be modified
by direct file edits or by re-generating via Optimizer / Generate.

---

## Generate Prompt (`/generate`)

Source: [Generate.razor](../Components/Pages/Generate/Generate.razor),
[PromptGeneratorService](../Services/PromptGeneratorService.cs)

A four-step `MudStepper` for creating a brand-new `.pmt` from scratch:

1. **Describe** — name, purpose ("what should this prompt do?"), guidance schema dropdown
   (None / json-auto / json / regex / gbnf), and a Request-JSON checkbox.
2. **Examples** — input/output pairs. Inputs are key/value dictionaries; each example has
   one or more inputs and an expected output. At least one example is required.
3. **Generate** — calls `PromptGeneratorService.GenerateStreamAsync`, which streams the
   `Optimizer.Generator` prompt's completion token-by-token. The user sees a blinking
   cursor while it streams.
4. **Save** — the generated `.pmt` text is editable. **Save to RConfigs** writes via
   `PromptRegistryService.SaveNew(name, content)`. **Test Now** jumps to
   `/test?prompt=<name>`.

The bundled `Optimizer.Generator` prompt
([RConfigs/Prompts/Optimizer/Generator.pmt](../RConfigs/Prompts/Optimizer/Generator.pmt))
includes a meta-description of the `.pmt` format and rules for "good prompts," so the
generator output usually parses cleanly back as a `.pmt`.

---

## Test Runner (`/test`)

Source: [Test.razor](../Components/Pages/Test/Test.razor),
[TestRunnerService](../Services/TestRunnerService.cs)

Run a single prompt against multiple models, multiple times each, in parallel.

**Configuration panel (left)**:

- Prompt selector (populated from `IPromptManager.GetAll()`; pre-filled from
  `?prompt=` query)
- Per-model checkboxes (populated from `IModelManager.GetAll()`; all selected by default)
- Runs per model (1–20)
- "AI analysis per result" checkbox (default on)
- Input rows (label/value), at least one
- Run/Stop button + indeterminate progress bar while running

**Results panel (right)**:

- Four summary cards: total runs, average TTFT, average total time, average quality
  (only when analysis was enabled)
- A `MudDataGrid` row per run: `#`, Model, TTFT, Total, Quality chip (color-coded
  ≥8 green / ≥5 warning / else red), Status icon (check/error), and output preview.
- Clicking a row opens an inline detail card with the full output and, if AI analysis
  ran, the four `AnalysisResult` fields: FulfilledRequest, QualityScore, Analysis text,
  Improvements text.

**How it runs.** `TestRunnerService.RunTests` creates an unbounded channel and spawns
one `Task.Run` per (model × run number) combo. Each task uses
`IInferService.CompletionStream` and times TTFT (the first token's `sw.Elapsed`) and
total time. On success, if `runAnalysis` is true, it calls
`infer.ToObject<AnalysisResult>("Optimizer.Analyzer", ...)` to produce the per-run
score. Results are pushed back via the channel as they complete so the UI updates
incrementally.

**Cancellation.** A linked `CancellationTokenSource` is cancelled on Stop, on a new run,
or when the page is disposed.

---

## Optimizer (`/optimize`)

Source: [Optimize.razor](../Components/Pages/Optimize/Optimize.razor),
[OptimizerService](../Services/OptimizerService.cs)

Three tabs implementing analyze → suggest → revise.

### Tab 1 — Analysis

- Pick a prompt and a single model.
- Configure 1–10 test runs and one or more inputs.
- Click **Analyze Results.** The page reuses `TestRunnerService.RunTests` with
  `runAnalysis = true`, but only keeps the `Analysis` field of each result.
- Right column shows aggregate stats (fulfillment %, average quality, run count) and an
  expansion panel per run with its `Analysis` and `Improvements` text.

### Tab 2 — Suggestions

- Click **Generate Suggestions** (from Analysis tab, or by switching tabs).
- `OptimizerService.GenerateSuggestionsAsync` aggregates every `AnalysisResult` into a
  single text block and calls `IInferService.ToObject<SuggesterResult>` on the
  `Optimizer.Suggester` prompt.
- Each returned `PromptSuggestion` (description, expected impact, affected section)
  becomes a checkboxed card. Select All / Deselect All are available.
- **Apply Selected Suggestions** advances to tab 3.

### Tab 3 — Apply & Iterate

- Side-by-side diff: **Original** (rendered from `PromptRegistryService.SerializePrompt`)
  vs **Revised** (streamed from `OptimizerService.ReviseStreamAsync`, which feeds the
  `Optimizer.Reviser` prompt with the current prompt and the selected suggestions).
- Three actions: **Accept & Save** (auto-increments the prompt version and writes the
  new `.pmt`), **Test Revised Prompt** (jumps to `/test?prompt=<name>`), and **Revise
  Again** (re-runs the reviser without re-analyzing).

The full loop is: run → score → aggregate → revise → save new version → run again. The
file gets overwritten in place, but `[[information]] version = N` is incremented so
history can be retrieved from version control.

---

## Agent Workshop (`/workshop`)

Source: [Workshop.razor](../Components/Pages/Workshop/Workshop.razor),
[AgentWorkshopService](../Services/Workshop/AgentWorkshopService.cs),
[Workshop models](../Services/Workshop/Models/WorkshopModels.cs)

The agent counterpart to the Optimizer. Four tabs.

### Tab 1 — Run

- Pick an agent from `IAgentManager.GetAll()`.
- Provide a free-text Task (becomes both `inputs["input"]` and `inputs["task"]` when
  neither is present in the additional inputs).
- Optional `Runs (parallel)` — 1 to 20. All runs use the same task and inputs and
  execute in parallel (`Task.Run` per run inside
  [AgentWorkshopService.RunMultiAsync](../Services/Workshop/AgentWorkshopService.cs)).
- Optional key/value additional inputs.

Below the controls, an aggregate stat card (Total runs, Completed, Failed, Success rate)
and one expansion panel per session. Each panel shows the session id, exit state chip,
event count, and a live `MudList` of `RlogEvent`s with step-colored chips (Start →
Primary, LlmRequest/Response → Info, Thinking → Tertiary, ToolCall/Result → Warning,
StateTransition → Secondary, End → Success, Error / GuardrailViolation → Error). Final
output appears in a monospace paper when the run ends.

The live updates come from the `IWorkshopEventBus` — `AgentWorkshopService` subscribes
to `runner.SessionId` *before* starting the run, and the bus fans every matching
`RlogEvent` to the UI.

### Tab 2 — Trace

A more detailed, hierarchical view of one selected session. Lists every `RlogEvent`
sorted by timestamp; each one expands to show tags, `object1`/`object2` payloads (these
are where structured run artifacts like LLM prompts, tool inputs/outputs, and final
content live), and timestamps with millisecond precision.

The Workshop tab populates this view automatically when a run starts; the History tab
populates it via `LoadSessionAsync(sessionId)`.

### Tab 3 — Evaluation

- **Evaluate N run(s).** Calls
  `AgentWorkshopService.EvaluateSessionsAsync(agent, [sessionIds...])`. For each
  session, the service fetches the recursive event tree, extracts the final output and
  end-event metadata (exit reason, total steps), builds a compact "activity log"
  projection, and submits the whole batch to the `AgentWorkshop.Evaluator` prompt.
- Returns an `AgentEvaluationResult` with verdict (`completed` / `partial` / `failed`),
  score, strengths, weaknesses, ranked **Recommendations**, and **Alternatives**
  (different strategies, not incremental tweaks).
- Each recommendation card has a **Generate Diff** button that streams a full revised
  `.agent` file from `AgentWorkshop.Reviser`.
- After the diff finishes streaming, **Approve & Save** writes the file back via
  `SaveAgentRevisionAsync`, which reloads `IAgentManager`. **Discard** throws it away.

### Tab 4 — History

A paginated table of prior sessions for the currently-selected agent, fetched from the
log viewer (`GetAgentSessionsAsync`). Columns: started, duration, exit reason, event
count, and a truncated final-output preview. **View** loads the session into Tab 2.

---

## Refinery (`/refinery`)

Source: [Refinery.razor](../Components/Pages/Refinery/Refinery.razor),
[RefineryApiEndpoints.cs](../Api/RefineryApiEndpoints.cs)

The control surface for the Refinery — the agent measurement-and-improvement toolkit.
Where the Agent Workshop is a single-run debugger, Refinery runs **campaigns**: an agent
executed against a scenario suite, scored on invariants / efficiency / an LLM judge, with
variant proposals accepted or rejected behind a regression gate. Concepts are documented
in [refinery-campaigns.md](refinery-campaigns.md); writing a plugin is documented in
[refinery-plugin-authoring.md](refinery-plugin-authoring.md).

**Plugin catalog.** One card per configured plugin repo (`Refinery:Repos`) showing status,
project path, agents, scenario suites, and invariant checkers, plus any build error or
warning. **Build & reload all** refreshes every repo; per-plugin **Reload** hot-reloads
one. Loaded plugins with suites get two actions: **Run baseline** (measure only) and
**Refine** (full proposal loop).

**Campaigns.** A table of every campaign (id, agent/suite, status, invariant pass-rate,
quality mean, runs). Clicking a row opens the detail view: baseline vs. current
aggregates, per-round iterations, the accept/reject ledger, and — for accepted variants —
a **Promote to agent** button that (after a confirmation dialog) writes the variant back
to the real agent definition. Promotion is the only step that touches production files,
and it is always human-initiated.

### Refinery Control API (`/api/refinery`)

The same backend the dashboard uses, exposed over HTTP for the
[`revi` CLI](revi-cli.md) and other clients. By default the group is **unauthenticated**
(local/operator use); set `Forge:RefineryApi:RequireApiKey = true` to guard every
endpoint with the same `X-Forge-ApiKey` validation the `/api/v1` gateway uses.

| Endpoint | What it does |
| --- | --- |
| `GET /plugins` | Plugin catalog (status, agents, suites, invariants). |
| `POST /plugins/refresh` | Rebuild + reload all plugin repos; returns the catalog. |
| `POST /plugins/{name}/reload` | Hot-reload one plugin. 404 if unknown. |
| `GET /campaigns` | List all campaigns. |
| `POST /campaigns` | Start a campaign from a `CampaignSpec` (202 with `{id, status}`). `AutoPropose=false` runs a baseline only. |
| `GET /campaigns/{id}` | Full campaign state (spec, status, iterations, aggregates). |
| `GET /campaigns/{id}/ledger` | Accept/reject ledger entries for the campaign. |
| `POST /campaigns/{id}/stop` | Request cancellation. 200 `{stopped:true}` when signalled; 404 unknown id; 400 already terminal. |
| `POST /campaigns/{id}/promote/{variantId}` | Promote an accepted variant to the real agent files. |
| `GET /meta` | Knob-effectiveness rollup mined from ledgers across campaigns (`?agent=` to scope). |
| `POST /optimize` | One-shot prompt optimize: run models×runs, analyze, suggest, return the revised `.pmt`. |
| `POST /test/run` | Run a saved suite by name (prompt- or agent-mode); returns the `SuiteRunSummary`. |
| `GET /calibration` | Confidence-vs-accuracy calibration report (`?agent=` required, `&version=` optional). |
| `POST /generate-scenarios` | LLM-author new evaluation scenarios for an agent/category. |

---

## Observer (`/observer`)

Source: [Observer.razor](../Components/Pages/Observer/Observer.razor),
[ReviLogFeed.razor](../Components/Observer/ReviLogFeed.razor),
[MongoReviLogViewerService](../Services/Observer/MongoReviLogViewerService.cs)

The general-purpose live log feed. Reads from the `LogEvents` Mongo collection.

**Left column.** `ReviLogInstancesList` — every distinct `MachineId/InstanceId` that has
ever emitted an event. Clicking one filters the feed.

**Toolbar.**

- Collapse/expand instances panel
- Agent name filter (`agent:<name>` tag match)
- Agent session id filter (`agent-session:<uuid>` tag match)
- Search box — with a Filter / Highlight toggle:
  - **Filter** mode (default): server-side `$text`-style search; wildcards (`*`, `?`)
    flip to client-side filtering on a larger page
  - **Highlight** mode: no filtering; query string is highlighted in the rendered rows
- Per-level chips (Trace / Debug / Information / Warning / Error / Critical) toggle
  level filters
- **Hide/Unhide…** opens `ReviLogLimiterDialog` for editing the suppression rules in
  `revilogger_limiter.txt` (class/method/line based)
- **Live / Pause** toggles auto-refresh (adaptive 500ms-2s)
- **Clear Logs** dropdown (instance-scoped): All, Older than 1 hour, Older than 1 day

**Main column.** `ReviLogFeed` renders the page. Each row is a `ReviLogRow` that uses
`LogFeedFlattener` to indent child events under their parent (so an `llm-request` event
nested inside an agent step appears one level deeper).

This is the only page that works against arbitrary instances — you can point a process
in production at the same Mongo and watch it from a dev Forge instance.

---

## Usage (`/usage`)

Source: [Usage.razor](../Components/Pages/Usage/Usage.razor),
[UsageDashboardService](../Services/Gateway/UsageDashboardService.cs)

Dashboard for traffic that went through the `/api/v1/infer` gateway.

**Time-range toggle.** 24h / 7d / 30d.

**Top metric cards.** Total requests, success rate, P50 latency, P95 latency. Latency
percentiles come from `UsageDashboardService` sorting the `LatencyMs` field of every
`ForgeUsageRecord` in the window.

**Breakdown tables.** "By Provider" and "By Model" — count of requests per name.

**Records table.** Every individual request: Time, Client, Prompt, Model, Provider,
Latency, Status chip.

The data comes from MongoDB (`ForgeUsage` collection) when available, otherwise the
in-memory ring buffer (≤10k records). Note that the ring buffer is intentionally small;
the Usage page is most useful when Mongo is configured.

---

## API Keys (`/apikeys`)

Source: [ApiKeys.razor](../Components/Pages/ApiKeys/ApiKeys.razor),
[ForgeApiKeyService](../Services/ApiKeys/ForgeApiKeyService.cs)

Lifecycle management for `X-Forge-ApiKey` credentials.

**Grid columns.** Client ID, Key Prefix (8 chars), Enabled switch, Created, Last Used,
Delete action.

**Generate New Key.** Prompts for a client id, calls `IForgeApiKeyService.CreateAsync`,
which generates a 32-byte cryptographic random key prefixed `forge_`, stores its SHA256
hash, and returns the raw key once. The raw key is shown in a modal with "Copy this key
now — it will not be shown again." That is the only chance to capture the raw secret.

**Toggle / Delete.** The `Enabled` switch and the delete button both invalidate the
service's `IMemoryCache` lookups (60-second TTL); a disabled key produces 401 on next
use. Delete is irreversible and asks for confirmation.

**Validation flow.** `IForgeApiKeyService.ValidateAsync(rawKey)` hashes the candidate
and looks it up. On hit, it returns the `ClientId` and fires `UpdateLastUsedAt` as
fire-and-forget. The cache short-circuits repeated validations of the same key.

---

## Inference gateway (`/api/v1`)

Source: [ForgeApiEndpoints.cs](../Api/ForgeApiEndpoints.cs),
[GatewayRouterService](../Services/Gateway/GatewayRouterService.cs)

Five HTTP endpoints. All except `/health` require the `X-Forge-ApiKey` header.

### `GET /api/v1/health`

Unauthenticated liveness check. Returns `{ "status": "ok", "timestamp": "..." }`.

### `POST /api/v1/infer`

Request body (`ForgeInferRequest`):

```json
{
  "ClientId": "BetterNamer-Prod",
  "PromptName": "Search.AnalyzeSpecs",      // optional — looks up by name
  "PromptContent": "...",                    // optional — ad hoc system content
  "Inputs": [{ "Label": "Task", "Text": "..." }],
  "MinTier": "B",                            // optional — Tier filter (A > B > C)
  "PreferredModels": ["gpt-4o-mini", "..."], // optional — explicit ordered preference
  "BlockedModels": ["..."],                  // optional — never route here
  "ExplicitModel": "claude-3-5-sonnet",      // optional — forces this model, no failover
  "Stream": true,                            // default true → SSE; false → JSON
  "GuidanceSchema": "json-auto",             // forwarded
  "CompletionType": "PromptOnly",            // forwarded
  "Temperature": 0.4,
  "MaxTokens": 1500,
  "InactivityTimeoutSeconds": 30
}
```

**Routing.** `GatewayRouterService.GetCandidates`:
1. If `ExplicitModel` is set, only that model is tried (no failover).
2. Else if `PreferredModels` is non-empty, only those models in that order are tried.
3. Else, every enabled model with an `InferenceClient` and `Tier >= MinTier`, ordered by
   tier.

For each candidate, the router:
- Skips it if it's in the 60-second cooldown set (failures populate this).
- Acquires the provider-level concurrency semaphore (`ForgeRateLimiterService` —
  defaults to 10 concurrent per provider).
- For streaming, opens the provider's `GenerateStreamAsync`; on success, emits SSE
  events of type `chunk` (per token), `done` (with model/provider/latencyMs), or
  `error`. On failure, releases the semaphore, marks the model in cooldown, and tries
  the next candidate.
- For non-streaming, calls `GenerateAsync` and returns the consolidated JSON
  `ForgeInferResponse` (Success, Output, ModelUsed, ProviderUsed, InputTokens,
  OutputTokens, ErrorMessage). HTTP 502 when no candidate worked.

Every attempt (success or final failure) writes one `ForgeUsageRecord` with token
estimates from `Util.EstTokenCountFromCharCount` plus `FailoverAttempts`.

### `POST /api/v1/usage/report`

For clients that want Forge's auth/routing decision but call the provider themselves —
they POST a `ForgeUsageReportRequest` after they finish so the Usage dashboard stays
complete. Body is essentially the same as the record minus the things Forge can fill in
(timestamp, api key prefix).

### `GET /api/v1/prompts` and `GET /api/v1/prompts/{name}`

Returns prompt names (list endpoint) or `{ name, system, instruction }` (single
endpoint, 404 if not found). Used by clients that want to discover what's available
without an out-of-band manifest.

### `GET /api/v1/models`

Returns every enabled model with `{ name, tier, provider, modelString }`, ordered by
tier then name.

### What is intentionally absent

- No per-client rate limiting or quotas.
- No multi-tenant data isolation.
- No webhook callbacks.
- No request retries beyond the failover loop.
- No structured output validation at the gateway (Core's analyzers handle that at the
  call site).
