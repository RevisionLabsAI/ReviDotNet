# Forge UI/UX Design Review — 2026-07-02

Scope: every reachable page of ReviDotNet.Forge, reviewed against the intended workflows in
[user-flows.md](../ReviDotNet.Forge/Docs/user-flows.md) (author → test → optimize prompts;
build → run → evaluate → refine agents; operate the gateway). Sources: the 20 snapshots in
`Docs/snapshots/`, the page/component code, and the design tokens in `Docs/mockups/tokens.css`.

Structure: cross-cutting issues first (they explain most per-page symptoms), then a
page-by-page analysis with concrete changes, with deep dives on the **agent instance view**
(explicit keep / relocate / merge / remove lists) and the **optimize + editing pages**.

---

## 1. Executive summary

The theming foundation is genuinely good — the token system (Inter, JetBrains Mono, the
indigo/cyan/violet accent family, soft pills, consistent radii) is coherent and the dark
theme is well executed. The problems are structural, not cosmetic:

1. **The instance view repeats itself.** Status is displayed at up to six nesting levels,
   step narrative text renders twice (collapsed title + expanded body), and sub-agents
   render their identity header twice back-to-back. Four levels of nested rounded cards
   ("box-in-box-in-box") add visual weight that the data doesn't need. Nothing needs to be
   *deleted from the data* — the fix is deduplication and demotion (section 4).
2. **The optimizer is a wizard disguised as tabs.** Analysis → Suggestions → Apply are
   sequential stages, but they're presented as three peer tabs that sit empty until earlier
   stages run. Prompt selection exists twice on the same screen (left list + dropdown in the
   Analysis tab). Results are ephemeral. (Section 5.5.)
3. **Editing surfaces leak the config layer.** Field labels are raw config keys
   (`request-json`, `api-url*`, `max-tokens`), agent authoring is a bare 24-line textarea,
   and Models/Providers "edit" pages are actually read-only viewers. The UI asks users to
   think in `.rcfg`/`.pmt`/`.agent` file terms rather than in tasks.
4. **Workflow handoffs are strong in some places (PromptEdit → Test/Optimize buttons,
   SessionViewer → "Evaluate these runs") and missing in others** — test results can't be
   sent to the optimizer, versions don't link to the evaluations that scored them, Refinery
   campaigns don't link back to agents, inference rows don't link to anything.
5. **Three visual systems coexist**: MudBlazor Material pages, the custom grouped-trace CSS
   (tokens.css-derived), and Observer's unstyled raw-HTML toolbar. The grouped-trace look is
   the best of the three and is worth promoting into the app-wide direction.

Priority order I'd suggest (matches your stated concerns): §4 instance view cleanup →
§5.5 optimizer restructure → §5.3/5.4 + §6.2 prompt/agent editing → cross-cutting nav and
consistency fixes → routing-page polish.

---

## 2. Cross-cutting issues (fix once, benefits every page)

### 2.1 Navigation shell

Current drawer: Dashboard, Observer, **Prompts** (Registry, Workshop, Test Runner,
Optimizer), **Agents** (Registry, Workshop, Session view (preview)), Refinery, **Routing**
(Clients, Models, Providers, Inference, Embeddings), Usage.

- **Duplicate child labels.** With both groups expanded (the default), the drawer shows two
  "Registry" and two "Workshop" entries simultaneously. Worse, the two Workshops are
  different concepts: Prompts→Workshop is a *create-a-prompt form*; Agents→Workshop is a
  *sessions/evaluations hub*. Rename Prompts→Workshop to **"New Prompt"** (or remove it —
  the registry already has a Create New button) and keep "Workshop" only for agents.
- **`Session view (preview)` is a dev artifact** (`/workshop/instance/preview`, synthetic
  data, "preview" chip). Remove it from the nav or gate it behind a debug flag. Real
  session views are reached through Workshop → session rows.
- **No breadcrumbs.** Deep pages (`/prompts/edit`, `/workshop/session/{id}`,
  `/workshop/evaluation/{id}`, `/models/edit`) rely on a lone back-arrow. A one-line
  breadcrumb (`Agents / researcher / v3` or `Workshop / Sessions / researcher — 2m ago`)
  would orient users and give click-up navigation for free.
- **Group by workflow, not by artifact, for the lab pages.** Test Runner and Optimizer are
  prompt tools; they're correctly under Prompts. But consider the label "Prompt Lab"
  pattern: Registry / New / Test / Optimize reads better than Registry / Workshop / Test
  Runner / Optimizer.

### 2.2 One design system

- **Adopt the tokens.css vocabulary app-wide.** The grouped instance view (pills, soft
  status chips, monospace ids, `--ev-*` event accents) is the most refined surface in the
  app. The MudBlazor pages are fine but visibly different (Material chips, different
  radii/typography rhythm), and Observer's toolbar is unstyled HTML (see
  `02-observer.png`: run-together "DebugInfoWarningErrorFatal" level labels, bare inputs).
  You don't need to rip out MudBlazor — restyle it via theme + CSS to match the tokens.
- **Tab styling is inconsistent and sometimes shouting.** `Workshop.razor:15` and
  `Optimize.razor:83` use `MudTabs Color="Color.Primary" Elevation="2"` which renders the
  full-width solid indigo band (see `06-optimizer.png`, `09-agent-workshop.png`), while
  PromptEdit/ModelNew/AgentEdit use quiet `Elevation="0"` tabs. Standardize on the quiet
  underline style; reserve saturated indigo fills for primary action buttons.
- **Field labels: humans first, config keys second.** `request-json`, `chain-of-thought`,
  `api-url*`, `model-string*`, `supports-prompt-completion` should be Title Case labels
  ("Request JSON output", "API URL") with the config key in the helper text or a tooltip
  (it's still discoverable for people hand-editing `.rcfg`). This is one sweep across
  PromptNew/PromptEdit/ModelNew/ProviderNew/AgentNew.

### 2.3 Shared components (kill the copy-paste variants)

- **Inputs key/value editor** exists in at least 3 near-identical implementations (Test,
  Optimize, PromptNew examples; NewSessionDialog/NewEvaluationDialog have a 4th/5th).
  Extract `<InputPairsEditor>`. Then give it the killer feature once: **derive the rows
  from the selected prompt's `{Placeholders}`** instead of making users remember key names
  (parse the instruction text; offer "+ add extra input" for unusual cases). This single
  change removes the most error-prone manual step in Flows 2 and 3.
- **Prompt picker** exists as registry grid, Test dropdown, Optimize dropdown, Optimize
  sidebar list. Extract one searchable picker (name + version + schema chip) used
  everywhere.
- **Empty states**: standardize one component (icon, one sentence, primary CTA). Clients
  has the best one ("No clients yet. Generate an API key to onboard a client."); Refinery
  has the worst (references "Run baseline on a loaded plugin **above**" when no plugin
  section is rendered above — see `12-refinery.png`).
- **Tables**: Models/Providers/Clients use MudDataGrid, Inference/Embeddings/Usage use
  MudTable, Observer is custom. Pick one (MudDataGrid), and give every list page the same
  toolbar: search left, filters middle, refresh + "updated Xs ago" + page-level CTA right.

### 2.4 Persistence and job feedback

- **Ephemeral results are a recurring trap**: Test Runner results, registry batch-analyze
  results, and optimizer analyses (in-memory WorkbenchState only) all evaporate on
  navigation/restart, while Workshop sessions/evaluations persist. Users learn one habit
  ("my runs are saved") and get burned by the other pages. Persist test runs and optimize
  runs the same way Workshop persists sessions (they're the same shape: config + N results
  + timestamps).
- **JobIndicator is good** — but pages don't say "this run continues if you leave." A tiny
  "runs in background — view in Jobs" hint next to Run buttons would let people navigate
  freely.

### 2.5 Cross-linking (make every noun clickable)

Missing links found: model row → its provider; inference/usage row → prompt / model /
provider / client; client row → its filtered inference view; dashboard Recent Activity
items → the artifact; VersionsHub best-score chip → the evaluation that produced it;
Refinery campaign → agent edit page (and agent version history → originating campaign);
session id in the instance-view header → Observer filtered to that session. Rule of thumb:
if a cell shows an entity's name, it links to that entity.

---

## 3. Dashboard (`/`, `01-dashboard.png`)

**Works:** clean layout; stat cards link to registries; live jobs mirror the JobIndicator;
provider health section exists.

**Issues**

- Counts are static after load; Recent Activity items aren't clickable; Provider health
  shows only the enabled flag (no latency/error/cooldown info), duplicating what the
  Providers page already says.
- The page answers "what exists?" but not "what should I look at?" — no failures surfaced,
  no cost/traffic summary, no in-flight campaign/session status.

**Changes**

1. Make Recent Activity rows link to the artifact (prompt/agent edit page), and add the
   *kind* of change ("optimized v2 → v3", "revision applied from evaluation").
2. Upgrade Provider health to real health: success rate + P95 latency + cooldown state over
   the last hour (data already exists for Inference/Usage pages).
3. Add two workflow tiles: "Running now" (sessions/campaigns/tests with links) and
   "Last 24h traffic" (requests, error count → links to Inference filtered to Failed).
4. Refresh counts on `NotifyChanged` events or on an interval — cheap and removes staleness.

---

## 4. Agent instance view (session trace) — deep dive

Files: `Components/Pages/Workshop/Instance/GroupedInstanceView.razor` + `GroupedStep`,
`GroupedCallRow`, `GroupedCallDetail`, `GroupedCubeGrid`, `GroupedMini`, `SegBadge`,
`StPill`, `Ic`; host pages `SessionViewer.razor` (real sessions), `InstancePreview.razor`
(demo), `ChatSessionView` (per-turn embeds). Snapshots `10-…` / `11-…`.

**Verdict:** the information architecture (run → activation → step → call, recursive
sub-agents, cube grid for >5 calls, auto-expand of running steps, flat-trace fallback) is
*right* and worth keeping wholesale. The overload comes from four sources: (a) the same
fact rendered 2–6 times, (b) heavyweight header, (c) four levels of nested rounded cards,
(d) internal jargon surfacing (cycle numbers, `READY` signals, "preview" chip). All the
underlying data can stay visible or one click away, per your constraint.

### 4.1 KEEP (unchanged, or styling-only)

| Element | Why it earns its place |
|---|---|
| Agent name + version pill + model pill + status pill (header) | Core identity; already compact |
| Task text | Essential context — but collapse to 2 lines with "more" when long |
| Per-activation grouping by state name (`plan`, `gather`, `verify`) | This is the agent's state machine made visible — the view's best idea |
| Step rows: number, title, duration, chevron | Good scan line |
| SegBadge (✓5 ×1 ⊘1) on multi-call steps | Dense, legible summary; keep exactly as is |
| Thinking block (italic, tinted) | Distinct and useful; keep |
| Tool call rows: icon, monospace name, one-line summary | Good |
| Input / Output / Error panels (monospace, capped height) | Good; add a copy button and a "wrap ⟷ raw JSON" toggle |
| Cube grid for >5 calls + "max N parallel" annotation | Scales beautifully; keep radio-select behavior |
| Recursive sub-agent rendering (GroupedMini steps numbered 3.1, 3.2…) | Keep the recursion — fix only the duplicated header (below) |
| Auto-expand running steps; expand/collapse all | Keep; also add per-activation expand toggles |
| AgentEventTrace fallback for unprojectable logs | Important safety net |
| Live pulsing dot on running status | Nice touch, keep |

### 4.2 MERGE / DE-DUPLICATE (the core fix)

1. **Step title vs. body text.** `StepView.Title` is the first line of the assistant
   message, and the expanded body renders the full message again — so short messages appear
   verbatim twice (see `11-session-view-expanded.png`: "Requested 7 fetches; the
   tool-call-limit capped it at 6." twice, "Delegating verification…" twice). Rule: expanded
   body renders **only the remainder** of the message after the title line; if there is no
   remainder, no body block at all.
2. **Sub-agent double header.** The call row shows `research/fact-checker · v2 · summary ·
   status`, and expanding it renders GroupedMini whose first line is the *identical*
   name/version/status header. Suppress GroupedMini's header when it's hosted inside a call
   row (pass `ShowHeader=false` down, same as ChatSessionView already does at the top
   level); the expanded content should begin directly with Step N.1.
3. **One status signal per row.** Today a fully-completed run shows "Done" pills on the
   activation, every step, every call, plus green step-numbers, plus the header pill.
   Adopt: **color carries status; text appears once per row-type.**
   - Call rows: drop the "Done" word — show duration only (`1.5s`), colored by status; keep
     the word only for `Failed` / `Running` / `Queued` / `Dropped` (the exceptional states).
   - Step rows: keep the StPill (it's the primary scan level), drop the *activation-level*
     "Done" pill and keep the activation's tinted dot + duration.
   - Header keeps the overall status pill. Net effect: completed runs become visually quiet;
     failures pop.
4. **Cycle chip.** `plan  [Cycle 1]` — the cycle number nearly always equals the
   activation's ordinal position. Fold it into one muted suffix on the state name
   (`plan · cycle 1`), no chip box. Only render it prominently when cycles revisit a state
   (`gather · cycle 4` after an earlier `gather · cycle 2` — that's when it's informative).
5. **Transition pill.** `→ READY → gather` floating between groups reads as noise and
   leaks the internal signal name. Relocate to a subtle footer line *inside* the activation
   card: `exit READY → gather` in small muted mono. Keep the signal name (it's real
   debugging info), lose the centered-pill prominence and the double arrow.
6. **Empty Output panel on running calls** (`11-…png` bottom): don't render the OUTPUT
   box until there's content — show a small `running…` shimmer line instead.

### 4.3 RELOCATE (keep the info, move it down-hierarchy)

| Element | From → To |
|---|---|
| 4 stat tiles (Steps / Tool calls / Sub-agents / Tokens) | Full-width tile row (~120px tall) → one inline stat strip in the header: `3 steps · 10 tool calls · 1 sub-agent · 7.1k tok`, icons optional. Frees ~40% of header height with zero information loss |
| Session id + started time | Header pills → smaller muted metadata line under the name; session id truncated with copy-on-click, and **linked to Observer filtered to that session** |
| Meters (budget bars — currently always empty in `InstanceMeta.Meters`) | Either populate (token/cost/step budget is exactly what belongs in a header) or delete the dead model field until Refinery budgets land |
| "The grouped session view, rendered through the live RlogEvent…" explainer + `preview` chip | Demo-only text → InstancePreview page only; never rendered by SessionViewer |
| Expand/Collapse all | Keep top-right, but make the bar sticky so it's reachable mid-scroll on long traces |

### 4.4 REMOVE (visual weight, not data)

- One nesting box. Currently: activation card → step card → call card → I/O panel, each
  with border+radius+background. Render **steps as rows separated by hairlines inside the
  activation card** (keep the colored step-number as the status anchor, add a thin left
  accent border), and keep real cards only for activations and expanded call details. This
  is the single biggest "looks cleaner" lever.
- The duplicated renderings from §4.2 (message body echo, mini header echo, redundant Done
  pills, empty output box).
- The standalone status dot before the state name in activation headers (pill/duration on
  the right is enough once color rules from §4.2.3 land).

### 4.5 ADD (small, high-leverage)

- **"Next failure" jump + filter chips** (`Errors · Tools · LLM · Sub-agents`) in the sticky
  toolbar — on a 200-event trace this is how people actually debug.
- **Per-step token/duration in the step row right side** where data exists (tokens
  currently only aggregate; the projector already walks per-call events).
- **Deep links**: `#step-3` anchors so an evaluation's weakness ("failed in verify") can
  link straight to the offending step.
- SessionViewer already tabs multiple runs — add a compact per-run summary row (status,
  duration, tool-call count) on each tab so you can pick the interesting run without
  clicking through all of them.

---

## 5. Prompt pipeline

### 5.1 Prompt Registry (`/prompts`, `03-prompts-registry.png`)

**Works:** clean table (Name / Ver / Schema chip / Examples), search, Create New; row →
edit page; hidden gem: multi-select batch analyze with per-prompt quality results.

**Changes**

1. Surface the workflow verbs: row hover actions or a kebab menu — **Test · Optimize ·
   Export** (today users must enter the edit page first; the user-flows doc even *claims*
   registry rows have Test buttons — the doc and UI drifted).
2. Batch analyze is invisible until you happen to multi-select. Add a "Batch analyze"
   button in the header (disabled with tooltip until selection), and **persist batch
   results** — they're currently ephemeral and read-only; add "open lowest-scoring in
   Optimizer" links per row.
3. Add columns that answer "which prompt needs attention": last modified, last test score
   (from persisted runs, §2.4), usage count last 7d (Inference data already has PromptName).
4. Group folder prefixes (`evaluator/…`) as collapsible sections or an indent — dots/slashes
   already imply hierarchy the flat list ignores.

### 5.2 Create New Prompt (`/prompts/new`, "Workshop", `04-prompts-workshop.png`)

**Issues:** Manual tab is a flat stack of unlabeled-section textareas with kebab-case
labels; guidance-schema dropdown sits top-right, disconnected from the Schema textarea it
governs at the bottom; `Save` bottom-left, disabled-gray; Generate tab (per user-flows'
4-step description) renders as one long form, and the generated `.pmt` looks read-only but
is editable; "Save to RConfigs" is jargon.

**Changes**

1. **Fold Manual creation into PromptEdit.** After "Create New" ask only name + (blank /
   from-template / generate); then land in the real editor (the app already navigates to
   `/prompts/edit` after save — skip the intermediate duplicate form entirely). One editing
   surface to maintain instead of two.
2. Make Generate a true 2-step flow: (1) describe + examples, (2) review streamed result in
   the *same editor component* as PromptEdit with an explicit "editable draft — not saved"
   banner, then Save / Regenerate. Label the save button "Create prompt".
3. Wire "Test now" on the success toast (Flow 1 step 5 promises this).

### 5.3 Prompt Edit (`/prompts/edit` — no snapshot)

**Works:** the strongest workflow header in the app (Back, version + dirty state, **Test /
Optimize / Export / Save**). Tabs: Content / Settings / Tuning / Source preview.

**Issues**

- Settings tab is 14 flat kebab-case fields mixing everyday knobs (request-json) with rare
  ones (retry-prompt, completion-type, min-tier). Tuning is 7 more numeric fields. No
  grouping, no defaults indicated, no explanation of interplay (e.g. guidance-schema-type
  vs Schema content).
- **No Examples editing.** The registry shows an Examples count; `.pmt` files carry
  examples; the generator consumes them — but the editor can't view/add/edit them.
- No version history tab (agents have VersionsHub; prompts have ArtifactHistoryService
  snapshots feeding dashboard Recent Activity, but no UI). No placeholder awareness (the
  Instruction's `{Inputs}` aren't extracted or validated anywhere).
- Plain textareas for prompt content — no monospace, no placeholder highlighting.

**Changes**

1. Reshape into a **Prompt Studio**: monospace editor pane (System / Instruction / Schema
   as labeled sections, `{Placeholder}` tokens highlighted) + right rail with grouped
   settings: *Output* (guidance schema + request-json + schema link), *Generation*
   (tuning params with defaults shown), *Reliability* (retries, require-valid-output,
   timeout, best-of), *Routing* (min-tier, filter). Collapsed groups show a summary chip of
   non-default values — all fields stay reachable, the common path stays small.
2. Add an **Examples tab** (the same key/value + expected-output cards Generate already
   has) — it's the missing CRUD for a first-class `.pmt` section.
3. Add **Versions tab** reusing VersionsHub against prompt history (diff + restore), which
   also fixes "optimizer overwrote my prompt and git is my only undo" from user-flows'
   aspirational list.
4. Add an inline **quick-test drawer** (one model, one input set, run → output/score in
   place) for smoke tests, keeping the full Test Runner for benchmarking. Placeholder
   extraction from the instruction pre-fills the drawer's inputs.

### 5.4 Test Runner (`/test`, `05-test-runner.png`)

**Works:** saved suites (save/load/delete config), streaming results, Stop, summary cards,
row → detail with analysis; `?prompt=` deep link in.

**Issues:** inputs are hand-typed key/value pairs with no knowledge of the prompt's
placeholders; model list is a raw checkbox column (fine for 2 models, unusable at 20);
results vanish on navigation; no history of past runs; no version shown (you can't tell
which prompt version a result tested); dead-end results (no optimize handoff); right panel
is a large empty box at rest.

**Changes**

1. **Derive inputs from the selected prompt** (§2.3) — pre-create one row per placeholder,
   flag missing ones before Run.
2. **Persist runs** and add a left-rail "Recent runs" list (same visual pattern as
   Workshop sessions): config summary, date, avg score. Selecting one re-hydrates results.
   This unlocks the natural follow-ups: re-run, compare two runs (v2 vs v3 of the prompt —
   the missing regression check called out in user-flows Flow 3), and export.
3. Add **"Optimize this prompt →"** on the results summary (carries prompt + the same
   inputs into the Optimizer's analysis config). Closes the loop user-flows describes but
   the UI doesn't provide.
4. Model picker: multi-select dropdown with provider/tier chips + "select all by provider".
5. Stamp results with prompt version (`researcher v3`) in the summary header.
6. Empty state: replace "Configure and run tests…" with a 3-bullet mini-guide (pick prompt →
   inputs auto-fill → Run) plus a sample-suite link.

### 5.5 Optimizer (`/optimize`, `06-optimizer.png`) — deep dive

**Works (under the hood):** the pipeline itself is excellent — multi-model analysis with
per-model breakdown, ranked suggestions with affected-section chips, streamed revision into
a side-by-side DiffViewer, auto version bump, post-save quality re-measure with delta, and
per-prompt state restore (WorkbenchStateService). The machinery deserves a better shell.

**Issues**

1. **Duplicate prompt selection**: left sidebar list *and* a Prompt dropdown inside the
   Analysis tab control the same value. The sidebar also duplicates the Prompt Registry
   page itself.
2. **Tabs-as-wizard**: Suggestions and Apply & Iterate look like peer tabs but are empty
   until prior stages complete; there's no visible gating, progress, or "you are here".
3. The full-width indigo tab band dominates the page (§2.2) and even spans over the
   results area.
4. Inputs again hand-typed; no cancel affordance for suggestion generation; "Generate
   Suggestions" is buried under the run expansion panels; Fork icon (top of sidebar) is
   unexplained; nothing persists to disk — no record that an optimization happened.
5. The three-column layout (sidebar + config + results) leaves the results pane a small
   fraction of the screen where the actual payoff (analyses, diffs) lives.

**Recommended restructure — one-page pipeline, not tabs:**

```
┌ Prompt header: researcher v3 · model(s) · [inputs ✓] · Run analysis ─ status ┐
│ 1 ANALYZE   summary stats · per-model cards · run panels (collapsible)       │
│ 2 SUGGEST   appears when analysis lands: suggestion cards + [Apply selected] │
│ 3 APPLY     diff (original ↔ streamed revision) · quality delta ·            │
│             [Accept & save v4] [Test v4 →] [Revise again]                    │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Vertical **stages on one scroll** with numbered section headers that show state
  (`2 · Suggestions — waiting for analysis` greyed). Auto-scroll to a stage as it
  activates. No hidden tabs, no dead clicks; the "wizard" becomes visible progress.
- **One prompt picker**: keep the left rail (it's useful for hopping between prompts with
  per-prompt state restore) and delete the in-tab dropdown; the rail selection becomes the
  page title. Rail rows show version + last score.
- Config (models, run count, inputs) compresses into the sticky stage-1 header row; inputs
  auto-derive from placeholders (§2.3).
- **Persist each cycle** as an "optimization run" artifact (analysis + suggestions +
  accepted diff + delta) with a small history list per prompt — the audit trail for "when
  did quality move and why". This also gives the registry its "last score" column and the
  dashboard a real activity feed.
- Keep "Revise Again", but also allow **suggestion re-selection before re-revising** (today
  it reuses the same set) and a cancel button on generation.
- Move Fork behind a labeled menu item ("Duplicate as new prompt…") with a naming dialog.

### 5.6 Prompt workflow wiring (Flows 1–3) after these changes

Registry (scores visible) → Edit (studio, quick-test) → Test (placeholder-aware, history,
"Optimize →") → Optimizer (visible pipeline, persisted cycles, "Test v4 →") → back to
Registry. Every hop exists as a button; no page dead-ends.

---

## 6. Agents & Workshop

### 6.1 Agent Registry (`/agents`, `07-agents-registry.png`)

Same pattern as prompt registry, same fixes: hover/kebab actions (**Run session · Evaluate ·
History · Export**), last-session status + best-eval-score columns, clickable Entry-state
chip is unnecessary — but the DESCRIPTION column is good. Add empty-state CTA.

### 6.2 Create New Agent (`/agents/new`, `08-agents-new.png`)

**Issues:** Manual tab = name + a 24-line bare textarea for a *state-machine file format*,
with the format's minimum requirements crammed into one helper sentence. No validation
until save; no structure feedback. Generate tab is better (purpose, expected I/O, tool
chips, streaming) but shares the "generated output looks read-only" ambiguity.

**Changes**

1. Give the textarea a **live parse panel**: as the user types (300ms debounce — the
   AgentEdit machinery already parses), render the Parsed-view summary alongside: entry
   state, states with tools, unreachable states, missing `[[_state.X.instruction]]` warnings.
   The skeleton button stays; authoring stops being blind.
2. Generate tab: "No tools registered" alert should link to docs on registering tools
   (GreatDebate agents need custom C# tools — a known trap).
3. Same as prompts: after create, land in AgentEdit; keep AgentNew minimal.

### 6.3 Agent Edit (`/agents/edit`)

**Works:** best-structured editor in the app — Source (view/edit toggle with highlighted
read view), Parsed view (entry/states/budget + per-state panels), Version History
(VersionsHub with lineage, per-version best-eval score, side-by-side diffs), plus header
buttons (Open in Workshop, Export, Save) and honest alerts for embedded/in-memory agents.

**Changes**

1. **State graph mini-visualization** in Parsed view (nodes = states, edges = transitions
   from the `[[_loop]]` graph). Even a simple auto-layout beats reading transition tables;
   it also becomes the natural place to click a state and see its instruction.
2. Link VersionsHub score chips to their evaluations (§2.5) and add **"Restore this
   version"** (writes old content as a new version — no manual copy-paste rollback).
3. Strengthen the in-memory trap: if `_inMemoryOnly`, make Save's confirm dialog say
   changes won't survive restart, or offer "Save a disk copy under RConfigs/Agents".
4. "Open in Workshop" should carry the agent: `/workshop?agent=researcher` (hubs already
   support agent filtering; today the button drops you unfiltered).

### 6.4 Workshop hubs (`/workshop`, `09-agent-workshop.png`)

**Works:** Sessions/Evaluations as two tabs with parallel anatomy (filter → list → New
button); session rows are information-rich (mode chip, task preview, run count, status);
evaluation rows show score + "applied" chip; dialogs are well-scoped (mode toggle, parallel
runs, attachments; evaluation from fresh runs or an existing session; auto-suggest toggle).

**Issues & changes**

1. The solid-indigo tab band (§2.2). Restyle quiet.
2. `WorkshopHandoff.PendingEvaluation` is a transient in-memory bridge — navigate away at
   the wrong moment and the spec is silently lost. Encode the spec in the URL or persist a
   draft evaluation row immediately (status `pending`), which also makes it visible in the
   list while streaming.
3. "Evaluate these runs" (SessionViewer) skips the evaluation dialog, so you can't toggle
   auto-suggest or adjust anything. Open the same NewEvaluationDialog pre-filled instead.
4. Session list: add date grouping (Today / Yesterday / Older) and a status filter; at
   50+ sessions the flat list will hurt.
5. Consider making Workshop's landing view a two-column overview (recent sessions | recent
   evaluations) instead of tabs — the two lists are the same workflow's two halves and
   users bounce between them.

### 6.5 Evaluation viewer (`/workshop/evaluation/{id}`)

**Works:** loading phases while streaming; verdict/score/success/duration stat row;
strengths & weaknesses; ranked recommendations; alternatives; proposed-revision diff with
Approve & Save / Discard; "applied as v#" chip after save.

**Changes**

1. **Link evaluation → sessions it scored** (chips per session → SessionViewer) and, for
   each weakness, deep-link into the trace step where it manifests when the evaluator
   provides one (§4.5 anchors).
2. Generate Diff per-recommendation is one-shot: allow generating diffs for two
   recommendations and switching between them (tabs above the DiffViewer) before approving
   — "which fix do I take" is the actual decision being made.
3. After Approve & Save, offer the natural next step inline: "Run 3 sessions on v4 now"
   (closes Flow 4's loop without re-entering dialogs).

### 6.6 Refinery (`/refinery`, `12-refinery.png`)

**Works (per code):** plugin cards with build/load status, Run baseline / Refine buttons,
campaigns table (invariant pass-rate, quality mean), campaign detail with baseline-vs-
current stats, per-round variant panels (knob chip, accepted/rejected, train vs held-out
scores, diff, Promote).

**Issues & changes**

1. Empty state references UI that isn't rendered ("a loaded plugin above") and points at
   `appsettings` editing. Make it a real onboarding card: what a plugin repo is, the config
   key, a "docs" link — and demote "Build & reload all" (currently the page's only bright
   button) until plugins exist.
2. **No live progress**: campaigns only update on manual Refresh. Poll or push while any
   campaign is `Running` and show a per-campaign progress line (round X/Y, runs done,
   tokens spent) — Refinery runs are exactly the long jobs users will stare at.
3. Campaign detail is modal-in-page below the table; give campaigns their own route
   (`/refinery/campaign/{id}`) so they're linkable from dashboards and agent history.
4. Cross-link: campaign header → agent edit page; promoted variant → the new version in
   VersionsHub (and the reverse: version rows show "from campaign abc123").
5. "Refine" launches with hardcoded spec (2 samples, 3 rounds, auto-propose). Expose those
   three numbers in a small confirm popover — the difference between a $2 and $40 run.

---

## 7. Observability & routing pages

### 7.1 Observer (`/observer`, `02-observer.png`)

The most functionally rich page (wildcard search, level filters, hide/unhide, adaptive live
polling, CSV export) with the least styled chrome — the toolbar is raw inputs and
run-together level labels.

1. Rebuild the toolbar with the shared components: styled inputs, level filter as toggle
   chips (color-coded like log rows), Hide/Unhide and Pause as icon buttons with tooltips,
   proper spacing.
2. Empty states: differentiate "no Mongo configured" (setup guidance) from "no instance
   selected" (arrow to the left panel) — today both read as a bare sentence.
3. Add "open in Workshop" affordance when a log row carries a session id (§2.5).

### 7.2 Clients (`/clients`, `13-clients.png`)

Best-in-class page already (stat cards, hierarchy rows with per-key toggle/delete, window
filter, good empty state). Only: link client → filtered Inference view; show per-key
last-used; and the one-time raw key modal should include the exact `X-Forge-ApiKey` curl
snippet (Flow 5 step 1–3 in one copy block).

### 7.3 Models & Providers (`14-…`, `15-…`, `16-…`, `17-…`)

1. **The "edit" pages are viewers.** Rename affordance accordingly ("View / Export"), or —
   better — implement editing: these are disk-backed `.rcfg` files exactly like prompts and
   agents which *are* editable. At minimum allow toggling `enabled` and editing costs/tier
   from the UI. The current alert ("export… or use Add New") pushes CRUD out of the product.
2. ModelNew's 5 tabs for one entity is heavy; General + a grouped "Advanced" accordion
   (overrides, chat shape) would halve perceived complexity. Same for ProviderNew's
   tri-state `supports-*` checkboxes — replace indeterminate states with explicit
   "inherit / yes / no" selects, kebab-case labels → Title Case (§2.2).
3. Add **"Test connection"** on provider create/view (validates key + lists reachable
   models) — removes the create-model-then-watch-it-fail loop.
4. Cross-links: model row's provider chip → provider page; provider page → "models using
   this provider".

### 7.4 Inference & Embeddings (`18-…`, `19-…`)

Solid pattern (stat cards, window + status filters, search, pagination, refresh). Fixes:
align the summary-card windows with the filter windows (cards show 1h/24h/7d, filters
24h/7d/30d); make rows expandable to show the failure reason + full routing story (candidate
models, cooldowns — Flow 5's 502 debugging); link Client/Prompt/Model/Provider cells; add
auto-refresh toggle like Observer.

### 7.5 Usage (`/usage`, `20-usage.png`)

Overlaps Inference heavily (same records minus tokens) while missing its namesake: **cost**.
Recommend refocusing Usage as the aggregate/cost dashboard: requests + success + latency
percentiles (keep), add cost by model/provider/client over time (model cost fields already
exist in ModelNew), drop the duplicate raw records table (link to Inference instead), and
paginate anything that remains.

---

## 8. Prioritized plan

**Quick wins (days, mostly CSS/markup):**
1. Instance view dedupe: message-body echo, sub-agent double header, status-pill demotion,
   inline header stats, transition-pill demotion, empty-output fix (§4.2–4.4).
2. Quiet tab styling on Workshop/Optimize; nav renames; remove preview route from nav.
3. Title-case labels sweep; standard empty-state component (incl. Refinery, Observer).
4. Registry hover actions (Test/Optimize on prompts; Session/Evaluate on agents).

**Core workflow (1–2 weeks):**
5. Optimizer one-page pipeline + single prompt picker + persisted optimization runs (§5.5).
6. Placeholder-derived inputs shared component (Test, Optimize, quick-test) (§2.3).
7. Test run persistence + history rail + "Optimize →" handoff (§5.4).
8. Prompt Studio: grouped settings, Examples tab, Versions tab (§5.3).

**Deeper investments:**
9. Agent state-graph visualization + live parse on create (§6.2–6.3).
10. Cross-link pass (§2.5) + evaluation↔version↔campaign↔session links.
11. Refinery live progress + campaign routes (§6.6).
12. Models/Providers real editing + test connection (§7.3); Usage→cost refocus (§7.5).
13. Observer toolbar rebuild on the shared component set (§7.1).

---

*Snapshots referenced by filename throughout are in `Docs/snapshots/`. Component paths are
relative to `ReviDotNet.Forge/`.*
