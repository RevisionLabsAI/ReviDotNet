# User flows

The intended end-to-end journeys through Forge, mapped to the pages and services that
support each step. These are the "happy paths" the UI is designed around — any one of
them can be entered partway and abandoned partway without state loss.

The five flows below cover what the codebase actually supports today; an additional
section at the bottom notes flows that are partially built and where the seams are.

---

## Flow 1 — Author a brand-new prompt

**Who.** A prompt engineer adding a new capability to their application.

**Pre-conditions.** Forge is running, the user knows what task they want the prompt to
do, and they can produce 1–3 input/output examples that demonstrate the desired behavior.

### Steps

1. **Start.** Click **Generate Prompt** in the nav drawer.
2. **Describe** (step 1 of the stepper):
   - Enter a hierarchical name like `Search.AnalyzeSpecs`. Dots become folders inside
     `RConfigs/Prompts`.
   - Pick a guidance schema (None / json-auto / json / regex / gbnf). For typed outputs
     consumed by `ToObject<T>`, **json-auto** is the most common choice.
   - Check **Request JSON output** if the prompt should always return JSON.
   - In **What should this prompt do?** describe the task, the inputs it will receive,
     the output shape, and any tone/constraints.
3. **Examples** (step 2):
   - For each example, add input rows (`Key` / `Value`) matching the placeholders the
     instruction will use, then fill in the **Expected Output**.
   - Add at least one example. Three is a reasonable target for the generator to
     extract a pattern.
4. **Generate** (step 3):
   - Click **Generate.** A streaming completion runs from `Optimizer.Generator` and the
     `.pmt` content scrolls in. The cursor (`▌`) marks live streaming.
   - Click **Regenerate** to retry with the same inputs.
5. **Review & Save** (step 4):
   - Edit the streamed `.pmt` content directly in the text area if needed.
   - Click **Save to RConfigs.** The file is written under `Forge:PromptsSourcePath`
     and reloaded into `IPromptManager`.
   - Click **Test Now** to jump to the Test Runner with this prompt pre-selected.

### What happens behind the scenes

- `PromptGeneratorService.GenerateStreamAsync` invokes the bundled
  `Optimizer.Generator` prompt with the user's description, examples, schema choice,
  and JSON-request flag as inputs. The prompt's `[[_system]]` contains a complete
  description of the `.pmt` format so the model produces a parseable result.
- `PromptRegistryService.SaveNew(name, content)` does
  `File.WriteAllText` and `IPromptManager.LoadFromFile`. The prompt is now visible to
  every other Forge page and to any inference call.

### Acceptance criteria

- The new prompt appears in the Registry grid with the chosen name and version 1.
- Clicking **Test** from the registry executes against a real model.
- Clicking **View** in the registry shows the system, instruction, and examples.

---

## Flow 2 — Test and benchmark a prompt across models

**Who.** Anyone who has a prompt and needs to know which model handles it best, or
whether a recent change improved things.

**Pre-conditions.** At least one prompt is loaded (from disk or Flow 1). At least one
provider has valid credentials (via the `PROVAPIKEY__<NAME>` environment variables
documented in [provider-files.md](../../ReviDotNet.Core/Docs/provider-files.md)).

### Steps

1. **Open the Test Runner.** Click **Test Runner** in the nav drawer, or **Test** on a
   prompt row in the Registry.
2. **Configure.** In the left panel:
   - Confirm the prompt selection (pre-filled if you came from Registry).
   - Tick the models you want to compare. All are selected by default.
   - Set **Runs per Model** — 3 is a reasonable starting point for stable averages.
   - Keep **AI analysis per result** checked if you want a 1-10 quality score per run.
   - Fill in the inputs that match the prompt's placeholders.
3. **Run.** Click **Run Tests.** The button changes to Running… and a progress bar
   appears. Results stream into the grid as they complete (one per model × run).
4. **Read the results.**
   - Four summary cards: total runs, average TTFT, average total time, average quality.
   - Each row shows TTFT, total time, a quality chip (≥8 green, ≥5 yellow, else red),
     a status icon, and a one-line output preview.
   - Click a row to see the full output and the analysis breakdown (Fulfilled / Quality
     / Analysis / Improvements).
5. **Iterate or save.** If a model is clearly underperforming, exclude it next time.
   If the prompt itself is underperforming, jump to **Optimizer** (Flow 3) with that
   prompt's name in the query string.

### Notes & gotchas

- Each run is independent and fully parallel — costs scale with `models × runs`.
- AI analysis adds an extra inference call per run (uses `Optimizer.Analyzer`). Disable
  it for cheap dry runs.
- Stop is a hard cancel via `CancellationToken`; in-flight provider calls are aborted
  if the SDK supports it.

---

## Flow 3 — Optimize an underperforming prompt

**Who.** Someone who has a prompt that works "well enough" but produces inconsistent
quality, occasional malformed outputs, or doesn't quite fulfill the instruction.

**Pre-conditions.** The target prompt exists. You have at least one realistic input set
you can use as a test fixture.

### Steps

1. **Open the Optimizer.** Click **Optimizer** in the nav, or the trending-up icon on a
   prompt row in the Registry.
2. **Tab 1 — Analysis.**
   - Confirm the prompt and pick a single model (pick the one you actually deploy with).
   - Set **Test Runs to Analyze** to 3–5.
   - Fill in inputs.
   - Click **Analyze Results.** The runner executes against the chosen model, scoring
     each output via `Optimizer.Analyzer`.
   - Read the aggregate stats (fulfillment %, average quality, run count) and expand
     individual runs to read their `Analysis` and `Improvements` text.
   - Click **Generate Suggestions** when the picture is clear.
3. **Tab 2 — Suggestions.**
   - `Optimizer.Suggester` aggregates all per-run analyses into 3–7 ranked, concrete
     suggestions. Each is a card with description, affected section
     (system / instruction / schema / settings / tuning), and expected impact.
   - Untick anything you disagree with. Use Select All / Deselect All if you want to
     start from a different baseline.
   - Click **Apply Selected Suggestions.**
4. **Tab 3 — Apply & Iterate.**
   - Watch the revised `.pmt` stream in next to the original on the right side of the
     split view.
   - When streaming ends, **Accept & Save** writes the revised content back to disk
     with `[[information]] version` auto-incremented.
   - **Test Revised Prompt** jumps to the Test Runner. **Revise Again** re-runs the
     reviser with the same suggestion set (useful if the first revision drifted).
5. **Loop.** Optionally, return to Tab 1 and run another analysis pass. Each cycle
   produces a new version of the prompt; the file always reflects the latest, but git
   history (or wherever `RConfigs/` is committed) preserves prior versions.

### What is *not* being done here

- There is no train/test split or held-out evaluation set. The same inputs that drive
  analysis drive revision; that means you can over-fit to specific inputs.
- There is no automated regression check — the user is expected to re-run the Test
  Runner after **Accept & Save** to confirm the revision is genuinely better.
- The optimizer does not modify `[[settings]]` or `[[tuning]]` even when suggestions
  flag them (the Reviser prompt is structured around `[[_system]]` and
  `[[_instruction]]`).

---

## Flow 4 — Build, test, and tune an agent

**Who.** Someone shipping an LLM agent (multi-step state machine) and trying to debug
why it gets stuck, picks the wrong tool, or fails on certain inputs.

**Pre-conditions.** At least one `.agent` file exists under `RConfigs/Agents/`. The
included `test/echo` agent is a useful smoke test. Tools referenced by the agent are
registered in `IToolManager`.

### Steps

1. **Open Agent Workshop.** Click **Agent Workshop** in the nav drawer.
2. **Tab 1 — Run.**
   - Pick an agent from the dropdown.
   - Enter a Task (the free-text input the agent will see as `inputs["input"]` and
     `inputs["task"]`).
   - Optionally add key/value extra inputs if the agent's instructions reference other
     names.
   - Set **Runs (parallel)** to 1 for a debugging run, or 3–5 for variance checks.
   - Click **Run.** Live trace appears below. Each run is its own expansion panel:
     - Header chip shows live state (`Info` while running, `Success` on Completed,
       `Error` on Failure/GuardrailViolation/LoopDetected).
     - The list inside the panel updates as ReviLog events stream in: start
       (Primary), llm-request/response (Info), thinking (Tertiary), tool-call/result
       (Warning), state-transition (Secondary), end (Success).
     - Final output appears in a monospace paper when the run ends.
3. **Tab 2 — Trace.** A deeper view of one selected run. Each event expands to show its
   tags and the structured `object1` / `object2` payloads — useful for inspecting raw
   LLM prompts and tool I/O.
4. **Tab 3 — Evaluation.**
   - Click **Evaluate N run(s).** `AgentWorkshop.Evaluator` analyzes every run holistically
     and returns:
     - Verdict (`completed` / `partial` / `failed`)
     - Score 0–10
     - Success rate across the runs
     - Strengths and Weaknesses (concrete observations)
     - 3–7 ranked **Recommendations** (incremental improvements)
     - 1–3 **Alternatives** (different strategies — splitting agents, swapping tools)
   - Each recommendation card has a **Generate Diff** button.
5. **Apply a recommendation.**
   - Click **Generate Diff** on the chosen recommendation. `AgentWorkshop.Reviser`
     streams a full revised `.agent` file that incorporates the recommendation while
     keeping the agent name, incrementing the version, and preserving the state graph.
   - **Approve & Save** writes the new `.agent` to disk and reloads `IAgentManager`
     so subsequent runs use the new revision.
   - **Discard** throws it away.
6. **Loop.** Run again with the new revision and compare success rate / score.
7. **Tab 4 — History.** Browse all prior sessions for this agent (paginated). Click
   **View** on any row to inspect that session in Tab 2.

### Notes & gotchas

- `SaveAgentRevisionAsync` only works for agents whose source file is on disk under
  `<content-root>/RConfigs/Agents/`. Embedded-resource agents (the bundled `echo`) are
  read-only and the call will throw.
- `EvaluateSessionsAsync` requires successful retrieval of ReviLog events from the
  viewer. Without MongoDB configured, evaluation cannot reconstruct the activity log
  and will fail to produce useful output — Workshop assumes Mongo is set up.
- Parallel runs all share the same agent definition; a revision saved during a multi-run
  doesn't retroactively affect runs that already started.

---

## Flow 5 — Operate a Forge gateway in production

**Who.** A platform owner who wants other applications in their environment to call LLMs
via Forge instead of provider SDKs directly.

**Pre-conditions.** Forge is deployed and reachable on a private network. MongoDB and
ideally FusionAuth are configured. At least one provider's `PROVAPIKEY__<NAME>`
environment variable is set on the Forge container so it can call upstream APIs.

### Steps

1. **Issue an API key.**
   - Open **API Keys** in the Forge UI.
   - **Generate New Key.** Enter a `ClientId` that identifies the consuming application
     (e.g., `BetterNamer-Prod`, `Search-Staging`).
   - Copy the raw key (`forge_…`) from the modal *immediately* — it is only shown once.
   - The grid will show the new key disabled-toggleable, with prefix and creation time.
2. **Configure the client app.** If the client is a **ReviDotNet.Core** consumer, you don't need
   to hand-roll HTTP at all — drop a `RConfigs/forge.rcfg` into the app and `ForgeManager` will
   auto-route `IInferService.Completion`/`CompletionStream` through the gateway (see the
   "Client configuration (`forge.rcfg`)" section in [configuration.md](configuration.md)). For
   non-Core clients, add the `X-Forge-ApiKey` header to every request and point the inference base
   URL at Forge's `/api/v1`, as below.
3. **Make a request.** From the client:
   ```http
   POST /api/v1/infer HTTP/1.1
   X-Forge-ApiKey: forge_…
   Content-Type: application/json

   {
     "ClientId": "BetterNamer-Prod",
     "PromptName": "Search.AnalyzeSpecs",
     "Inputs": [{ "Label": "Specs", "Text": "…" }],
     "MinTier": "B",
     "Stream": true
   }
   ```
4. **Observe.**
   - Stream consumers see SSE events: `event: chunk` (per token), `event: done` (final
     metadata), `event: error` (terminal failure).
   - Non-stream consumers get a single JSON `ForgeInferResponse`.
5. **Watch traffic.** Open **Usage** in Forge.
   - The card row shows total requests, success rate, and P50/P95 latencies for the
     last 24h / 7d / 30d.
   - The breakdown tables show traffic by provider and by model — useful for spotting
     failover patterns ("everything is hitting Claude because OpenAI is cooling
     down").
   - The records table is the per-request audit trail.
6. **Watch behavior.** Open **Observer.**
   - Pick the Forge instance from the left panel, or a different instance for any
     client app that's also publishing ReviLog events to the same Mongo.
   - Use the search/level/agent filters to drill in. Use **Hide/Unhide…** to silence
     noisy classes.
7. **Manage keys.**
   - Toggle a key's **Enabled** switch to revoke it without losing the audit record.
   - Delete it permanently when retired.
   - Validation results are cached for 60 seconds — a revoked key may continue to work
     for up to that long.

### Failure-mode reading

- HTTP **401** — bad / disabled / missing `X-Forge-ApiKey` (authentication only).
- HTTP **400** — malformed JSON body, or a missing `ClientId` in the body (this check runs
  *after* authentication succeeds, so it's a 400, not a 401).
- HTTP **502** — every candidate model failed or is in cooldown. The 60-second cooldown
  is per-model; check Observer for the underlying provider errors and Usage for the
  failover pattern.
- SSE `event: error` — same as 502 but mid-stream.

---

## Partial / aspirational flows

A few flows are foreshadowed in the code but aren't reachable through the UI yet:

- **Browse RConfigs directly** — there is no Providers or Models management screen. Both
  are loaded from disk (`RConfigs/Providers/*.rcfg`, `RConfigs/Models/*.rcfg`) at startup
  and only exposed read-only via `GET /api/v1/models`. Editing requires direct file edits.
- **Rate limit per client** — `ForgeRateLimiterService` is keyed by provider, not by
  `ClientId`. There is no per-key budget.
- **Promote a revision** — the optimizer / workshop write back over the same file. There
  is no concept of "promote v3 to production" or "rollback to v2." The `[[information]]
  version` increments but version 1 is overwritten by version 2 in place. Source control
  is the only history.
- **Tier-based routing across providers** — `MinTier` works, but `Tier` is a single
  ordinal property on a model. There is no policy for "prefer cheaper for batch jobs,
  faster for interactive" beyond what callers send in `PreferredModels`.

These are observations, not criticisms — they're consistent with Forge being early in
its lifecycle. See [roadmap.md](roadmap.md) for more.
