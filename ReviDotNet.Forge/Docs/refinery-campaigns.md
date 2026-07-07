# Refinery Campaigns: measuring and improving agents

A Refinery campaign is a closed measurement-and-improvement loop for an agent. It runs the
agent against a fixed scenario suite to establish a baseline, then repeatedly proposes small
revisions to the agent's definition, re-measures each candidate, and only accepts a revision
that passes a strict, deterministic regression gate. Everything the campaign tries — accepted
or rejected — is recorded in an append-only ledger, and nothing touches your agent's source
file until a human explicitly promotes an accepted variant.

**Prerequisites.** Forge is running, and a Refinery plugin is loaded that supplies the agent,
its scenario suites, and its invariant checkers — see
[refinery-plugin-authoring.md](refinery-plugin-authoring.md) for how to build one. The `revi`
CLI talks to Forge over HTTP (`--url` flag, `FORGE_URL` env var, default
`http://localhost:5000`).

Source: [RefinementController.cs](../../ReviDotNet.Refinery/RefinementController.cs),
[GatePolicy.cs](../../ReviDotNet.Refinery/Scoring/GatePolicy.cs),
[RefineryCampaignService.cs](../Services/RefineryCampaignService.cs),
[RefineryApiEndpoints.cs](../Api/RefineryApiEndpoints.cs)

---

## Concepts

- **Scenario** — one evaluation case for an agent: named `Inputs` passed to the run, an
  optional `WorldSeed` (interpreted by the plugin to seed its isolated test store), a
  `Rubric` (quality facet names for the LLM judge, e.g. `Groundedness`, `Neutrality`),
  `ExpectedInvariants` (which of the plugin's checkers should hold), optional `GroundTruth`
  (the known-correct answer, used by calibration analysis), and an optional `ReplayScript`
  (see [Replay mode](#replay-mode-deterministic-zero-cost-runs)).
- **Suite** (`ScenarioSuite`) — a named set of scenarios for one agent, supplied by the
  plugin.
- **Invariant** — a structural, pass/fail check on the run trace (an `IInvariantChecker`
  from the plugin). Invariants are the hard floor: a candidate that regresses any of them is
  rejected regardless of quality gains.
- **Rubric** — the soft, judged dimension. When a scenario has rubric facets, an LLM judge
  scores each run 1–10 per facet; the campaign aggregates these into a quality mean and a
  **p10** (10th percentile — the worst case a user is likely to see). The gate always works
  on p10, never the mean, to resist sampling noise.
- **Train vs held-out** — scenarios with `HeldOut = true` are excluded from proposal
  generation and used only for validation. This is the over-fitting defense: the proposer
  never sees held-out failures, but a candidate must not regress on them. If a suite has no
  held-out scenarios, held-out metrics mirror the train metrics (the held-out checks
  degenerate to train checks) — so add held-out scenarios if you care about generalization.

Types: [Campaign.cs](../../ReviDotNet.Refinery.Sdk/Campaign.cs),
[Scenarios.cs](../../ReviDotNet.Refinery.Sdk/Scenarios.cs)

---

## Baseline vs full campaign

Both are started the same three ways; the difference is a single switch (`AutoPropose`).

- **Baseline only** (`AutoPropose = false` / `--baseline-only`) — run every scenario
  `SamplesPerScenario` times, score each run (invariants + judge + efficiency), aggregate,
  and stop. Use this to find out where an agent stands before spending tokens on refinement.
- **Full campaign** (`AutoPropose = true`, the default) — measure the baseline, then run the
  improvement loop below.

### Via the CLI

```bash
# Baseline only
revi refine run --plugin greatdebate --agent FactChecker --suite factcheck-core --baseline-only

# Full refinement campaign: 3 samples per scenario, 500k agent-token budget, up to 8 rounds
revi refine run --plugin greatdebate --agent FactChecker --suite factcheck-core \
    --samples 3 --budget 500000 --max-rounds 8

# Deterministic scripted run (scenarios must carry a ReplayScript)
revi refine run --plugin greatdebate --agent FactChecker --suite factcheck-replay --mode replay
```

`refine run` starts the campaign, polls every 3 seconds until it reaches a terminal status
(30-minute watch timeout), and prints a summary: rounds run, variants proposed/accepted, and
final-vs-baseline quality p10 and invariant pass-rate deltas. Ctrl-C interrupts the *watch*
only — the campaign keeps running server-side.

### Via HTTP

`POST /api/refinery/campaigns` with a `CampaignSpec` body. Returns `202 Accepted` with the
campaign id immediately; poll `GET /api/refinery/campaigns/{id}`.

```json
{
  "PluginName": "greatdebate",
  "AgentName": "FactChecker",
  "SuiteName": "factcheck-core",
  "SamplesPerScenario": 3,
  "Mode": "live",
  "TokenBudget": 500000,
  "MetaTokenBudget": 200000,
  "MaxRounds": 8,
  "StopAfterNoImprovementRounds": 2,
  "AutoPropose": true
}
```

Every field except the first three has a default: `SamplesPerScenario` 3, `Mode` `"live"`,
budgets null (unbounded), `MaxRounds` 10, `StopAfterNoImprovementRounds` 2, `AutoPropose`
true. Note the CLI's `--budget` flag sets `TokenBudget` only; to set `MetaTokenBudget` use
the HTTP API directly.

### Via the dashboard

The **Refinery** page (`/refinery`) lists loaded plugins with their agents, suites, and
invariants. Each plugin card has **Run baseline** and **Refine** buttons; the campaigns table
below polls status, expands rounds/variants, and carries the **Promote to agent** button for
accepted variants.

**One at a time.** Forge serializes campaigns behind a run gate — a second campaign started
while one is running queues until the first finishes. Each campaign gets its own tool
registry (the plugin's tools are registered into a fresh per-run `IToolManager`, never the
shared one) and candidates run as parsed profiles, so no process-wide registry is mutated.
If the plugin implements `IScenarioWorld`, its store is reset once before the run and
re-seeded before every sample.

---

## The improvement loop, round by round

Each round of a full campaign:

1. **Build the candidate beam.** One LLM proposal (the `Evaluator.Proposer` prompt sees the
   current definition, the aggregate train scores, failing invariants, and quality
   weaknesses — *train data only*) plus every applicable typed knob mutator applied to the
   current definition. The LLM proposer picks from a fixed knob menu:

   > `system-prompt | state-instruction | few-shot | sampling | guardrail | state-graph | model | tool-gating`

   The typed mutators are cheap deterministic edits that fire only when the round's scores
   show a weakness their knob can plausibly fix:
   - **SamplingMutator** — lowers `temperature` by 0.1 (floored at 0.0) when invariants fail
     or quality mean is below 8.
   - **GuardrailMutator** — raises `max-steps` by ~25% (at least +1) when cards show
     termination/loop/step-limit failures.
   - **SystemPromptMutator** — appends one corrective clause to the `[[_system]]` block,
     derived from the most common failing invariant (grounding, neutrality, scope, …).

   A mutator returns nothing when its knob is absent or no useful change applies; the beam
   is capped at 16 candidates. A round with an empty beam counts as a no-improvement round.
2. **Validate** each candidate (`CandidateValidator`) — a candidate that doesn't parse as a
   well-formed agent is rejected before any tokens are spent running it.
3. **Score** the candidate on train and held-out (same samples-per-scenario as the
   baseline), running the parsed candidate profile directly with per-run tool isolation.
4. **Pairwise gate** — an LLM judge compares baseline vs candidate outputs per train
   scenario (capped at 8 scenarios) and returns a net: candidate wins minus baseline wins.
5. **Gate decision** (`GatePolicy`, below). Every candidate — pass or fail — is recorded as
   a `VariantRecord` and a `LedgerEntry`.
6. **Adopt the best.** Among the candidates that passed the gate, the one with the highest
   train quality p10 wins (tie-break: higher pairwise net). Its revised definition becomes
   the new current definition and its scores become the new baseline for the next round.
   If nothing passed, the no-improvement counter ticks up.

The loop ends when `MaxRounds` is reached, `StopAfterNoImprovementRounds` consecutive rounds
accept nothing (default 2), either token budget runs out, or the campaign is stopped.

---

## The acceptance gate, in plain language

`GatePolicy.Decide` is a pure function shared verbatim by the loop and the unit tests. A
candidate is accepted only if **all four** hold:

1. **No invariant regression** — the invariant pass rate must not drop on train *or*
   held-out, **and** no individual invariant id that was passing may drop to a lower pass
   rate on either set (an invariant missing from the candidate's results counts as fully
   regressed).
2. **Train quality strictly improves** — candidate train p10 must be strictly greater than
   baseline train p10.
3. **Held-out quality doesn't regress** — candidate held-out p10 must be at least the
   baseline held-out p10.
4. **Pairwise net is positive** — the head-to-head judge must prefer the candidate's outputs
   more often than the baseline's on train scenarios.

Every comparison uses lower bounds (pass rates, p10) rather than means, with a tiny epsilon
so floating-point noise never flips a decision. Rejections name the first failed condition —
that text lands in the ledger as the reject reason.

---

## Dual token budgets

A campaign burns tokens in two distinct places, tracked by two independent governors:

| Budget | Spec field | What it counts |
|---|---|---|
| Agent budget | `TokenBudget` | Agent-execution tokens: input + output tokens of every scenario run (baseline and candidates), read from the run trace. |
| Meta budget | `MetaTokenBudget` | The refinement machinery's own LLM calls: the quality judge, the pairwise gate, and the proposer. |

Either budget being `null` means unbounded on that axis. Exhausting **either** one
terminates the campaign with status `BudgetExhausted` — checked at the top of each round and
before each candidate, so a round cannot blow the budget mid-beam. The campaign summary
reports both spends separately.

Practical guidance: `TokenBudget` scales with `scenarios × samples × candidates-per-round ×
rounds`; the meta side is usually smaller but grows with rubric-heavy suites (one judge call
per run) — a `MetaTokenBudget` around half the agent budget is a reasonable starting cap.
Replay-mode runs spend no live agent tokens, but the judge/pairwise/proposer still run live,
so the meta budget is the one that matters there.

---

## Campaign lifecycle

```
Pending ──▶ Running ──▶ Converged          (loop finished normally)
                    ├─▶ BudgetExhausted    (either token budget ran out)
                    ├─▶ Stopped            (cancelled via stop)
                    └─▶ Failed             (unhandled error; Error carries the message)
```

`Pending` is the pre-created record you can poll immediately after start (a campaign can sit
Pending while queued behind another). Baseline-only runs use the same statuses — a completed
baseline lands in `Converged`.

Check status any time:

```bash
revi refine status <id>     # one campaign
revi refine list            # all campaigns
```

---

## Stopping a campaign

```bash
revi refine stop <id>
```

or `POST /api/refinery/campaigns/{id}/stop`. Responses: `200 {"stopped": true}` when a live
campaign was signalled (it lands in `Stopped` shortly), `404` for an unknown id, `400` when
the campaign already reached a terminal state. Stop also works on a campaign still *queued*
behind another — it will never start running. Progress made before the stop (completed
rounds, ledger entries) is preserved.

---

## Reading the ledger

Every candidate attempted — accepted, gate-rejected, or validator-rejected — appends one
immutable `LedgerEntry`: round, knob type, diff, train and held-out scores, accepted flag,
reject reason, and cumulative tokens spent.

```bash
revi refine ledger <id>            # table: round, knob, accepted, reason, scores, tokens
revi refine ledger <id> --json     # the raw LedgerEntry array
```

HTTP: `GET /api/refinery/campaigns/{id}/ledger`. The ledger is also the corpus the
knob-effectiveness meta-analysis mines — see
[refinery-advanced.md](refinery-advanced.md#knob-effectiveness-meta-analysis).

---

## Promoting an accepted variant

Accepting a variant during the loop changes nothing on disk — the campaign works on
in-memory copies. Applying a winner to the real agent is a separate, human-gated step:

- Dashboard: expand the campaign on `/refinery`, click **Promote to agent** on an accepted
  variant (confirmation dialog).
- HTTP: `POST /api/refinery/campaigns/{id}/promote/{variantId}`.

`PromoteVariantAsync` then:

1. Refuses unless the variant is marked accepted.
2. Resolves the agent's writable `.agent` source file (`AgentProfile.SourcePath`). Agents
   with no on-disk source (embedded resources) cannot be promoted.
3. Writes the variant's full `RevisedContent` over the source file. (Older records that
   stored only a diff get the diff applied to the current source; if it doesn't apply
   cleanly, nothing is written.)
4. Re-parses just that agent and swaps the updated profile into the live registry — the next
   run uses the promoted revision immediately, no restart.

The write is all-or-nothing: any failure returns `false` (HTTP 400) with a logged reason
rather than risking a corrupt agent file. As with the Optimizer, the file is overwritten in
place — source control is the version history.

---

## Replay mode: deterministic, zero-cost runs

Set `Mode: "replay"` (CLI `--mode replay`) and give scenarios a `ReplayScript` — a list of
scripted assistant turns. Each turn supplies what one LLM call would have returned: a
transition `Signal` (e.g. `CONTINUE`, `DONE`), the step `Content`, optional `ToolCalls`
(each emitted with an empty input, enough to exercise the tool-dispatch path), and
`PromptTokens`/`CompletionTokens` for cost-tracking fidelity.

At run time the agent-under-test is wired to a scripted model
([ReplayInference.cs](../../ReviDotNet.Core/Inference/ReplayInference.cs)): every LLM call
the agent makes consumes the next scripted turn, in order, with no network I/O. If the agent
takes more steps than scripted, the final turn repeats — so an over-running agent degrades
predictably instead of failing.

**When to use it.**

- **CI determinism** — the agent loop, state graph, tool dispatch, and invariant checkers
  are exercised against byte-identical outputs on every run, so structural regressions fail
  the build without flakiness.
- **Zero inference cost for the agent under test** — no provider calls, no agent-token
  spend. (The judge/pairwise/proposer still run live if the campaign refines; a
  baseline-only replay run of a rubric-free suite costs nothing at all.)

Scope: replay only applies to scenarios that actually carry a non-empty `ReplayScript`; a
scenario without one runs live even in replay mode. And because the script fixes the
*assistant's* outputs, replay measures the machinery around the model, not the model — a
candidate that only changes prompts will score identically in replay, so refinement
campaigns belong in live mode.
