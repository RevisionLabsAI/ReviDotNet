# Refinery Advanced: calibration, meta-analysis, scenario generation

Beyond the campaign loop itself ([refinery-campaigns.md](refinery-campaigns.md)), the
Refinery ships three analysis surfaces: **calibration** (does a fact-checking agent's stated
confidence match its actual accuracy?), **knob-effectiveness meta-analysis** (across every
campaign ever run, which kinds of changes actually get accepted?), and **scenario
generation** (LLM-authored evaluation cases to grow a suite). All three work off the
campaign store, so this doc also covers making that store durable with Mongo.

**Prerequisites.** Forge is running with a Refinery plugin loaded (see
[refinery-plugin-authoring.md](refinery-plugin-authoring.md)), and you have some campaign
history to analyze — calibration and meta-analysis are only as good as the corpus behind
them.

Source: [CalibrationAnalyzer.cs](../../ReviDotNet.Refinery/Analysis/CalibrationAnalyzer.cs),
[MetaAnalyzer.cs](../../ReviDotNet.Refinery/Analysis/MetaAnalyzer.cs),
[ScenarioGenerator.cs](../../ReviDotNet.Refinery/Analysis/ScenarioGenerator.cs),
[MongoCampaignStore.cs](../../ReviDotNet.Refinery/Store/MongoCampaignStore.cs)

---

## Calibration: confidence vs accuracy

A fact-checker that says "confidence 5" and is right 60% of the time is worse than one that
says "confidence 3" and is right 60% of the time — the second one is *calibrated*.
Calibration analysis joins each run's `FactCheckerDetermination` (winner + self-reported
confidence 1–5) against the scenario's `GroundTruth` and reports whether stated confidence
tracks actual accuracy.

```bash
revi calibrate --agent FactChecker
revi calibrate --agent FactChecker --version v3   # restrict to one variant
```

HTTP: `GET /api/refinery/calibration?agent=FactChecker&version=v3` (version optional).

Without `--version`, the report covers every captured run of the agent — the baseline plus
every candidate variant a campaign tried. With `--version <variantId>` it narrows to a single
variant: each candidate a campaign scores files its cards under the agent name with the
`VariantRecord` id as the version, so you can calibrate a specific proposed change. Use the id
from `revi refine ledger <campaignId>` (or the campaign's variant records).

### How it computes

- Runs are kept only when they have **both** a determination and a ground truth to compare
  against; a run is *correct* when its winner matches the scenario's `GroundTruth`
  (case-insensitive).
- Runs are grouped into buckets by confidence level 1–5. A well-calibrated agent reporting
  confidence `b` should be right **(b − 0.5) / 5** of the time — the midpoint of the
  bucket's range (confidence 5 → 90%, confidence 3 → 50%, confidence 1 → 10%).
- **ECE** (Expected Calibration Error) is the run-weighted sum of each bucket's gap between
  actual and expected accuracy: `Σ (n_b / N) · |acc_b − (b − 0.5) / 5|` over non-empty
  buckets. 0 is perfect; anything above ~0.15 means the confidence numbers shouldn't be
  trusted at face value.
- **Monotonic** is true when accuracy never *decreases* as confidence rises across the
  populated buckets — higher stated confidence never scores worse than lower. (Vacuously
  true with fewer than two non-empty buckets.)

### Reading the report

```
Calibration report: FactChecker
  Runs (with truth): 120   Correct: 87

  CONFIDENCE    RUNS  CORRECT  ACCURACY    W-ERROR
  ----------------------------------------------------
           2      10        4     40.0%     0.0083
           3      30       17     56.7%     0.0167
           4      50       38     76.0%     0.0417
           5      30       28     93.3%     0.0083

  ECE              : 0.0750
  Monotonic        : yes
```

Read it as: the agent is slightly overconfident at level 4 (76% actual vs 70% expected is
fine; the biggest weighted gap is there because half the runs land in that bucket), the
curve rises cleanly with confidence, and an ECE of 0.075 is healthy. A **non-monotonic**
curve (say level 5 scoring below level 3) is the red flag — it means the confidence field is
noise, and downstream logic that branches on it (e.g. "auto-accept at confidence ≥ 4")
is unsafe.

### Requirements and caveats

Calibration needs two things:

1. **Scenarios with `GroundTruth`** — no truth, no accuracy. Scenarios without it are
   silently skipped.
2. **A campaign store that captures per-run score cards** — the base `ICampaignStore`
   contract persists only campaigns and the ledger; calibration needs the individual
   `ScoreCard`s (with their determinations) plus the scenarios' ground truth. Both built-in
   stores (in-memory and Mongo) implement the `IScoreCardSource` capability, so the engine
   captures score cards + ground truth automatically during every campaign — no extra setup.
   Run at least one campaign (baseline or full) over ground-truth scenarios first; the
   endpoint returns an **empty report** (`TotalRuns: 0`) only until cards for that agent
   exist. A custom store that does *not* implement `IScoreCardSource` opts out of calibration
   (it always reports empty).

   > **Note:** the in-memory store keeps cards only for the process lifetime — set
   > `Forge:CampaignStore=mongo` (see [configuration.md](configuration.md)) for calibration
   > history that survives restarts.

---

## Knob-effectiveness meta-analysis

Every campaign appends one ledger entry per candidate tried, tagged with its knob class
(`system-prompt`, `sampling`, `guardrail`, …). The meta-analyzer mines that ledger **across
all campaigns in the store** and answers: for this agent, which kinds of changes actually
get accepted, and how good are the accepted ones?

HTTP only (no CLI verb yet):

```bash
curl "http://localhost:5000/api/refinery/meta"                     # all agents
curl "http://localhost:5000/api/refinery/meta?agent=FactChecker"   # one agent
```

Returns one row per `(agent, knob-type)`, ordered by acceptance rate descending, then
attempts:

```json
[
  {
    "agentName": "FactChecker",
    "knobType": "system-prompt",
    "attempts": 14,
    "accepted": 6,
    "acceptanceRate": 0.4286,
    "acceptedMeanQualityP10": 7.2
  },
  {
    "agentName": "FactChecker",
    "knobType": "sampling",
    "attempts": 9,
    "accepted": 1,
    "acceptanceRate": 0.1111,
    "acceptedMeanQualityP10": 6.5
  }
]
```

`acceptedMeanQualityP10` is the mean train quality-p10 over the accepted attempts (falling
back to held-out when a train score is absent), i.e. how good the wins were, not just how
often they happened.

**How to use it.** The table is the empirical answer to "where should the next campaign
spend its beam?" A knob with many attempts and a near-zero acceptance rate is wasted budget —
for the sampling row above, temperature nudges almost never survive the gate for this agent,
while system-prompt edits pay off both often and well. Concretely you can: prune or reorder
the typed mutators for that agent, steer the proposer prompt toward the paying knobs, or
simply read it as a diagnosis ("this agent's problems are instruction problems, not sampling
problems"). Since the analysis spans the whole store, it gets sharper with every campaign —
provided the store is durable (see below).

---

## Generating new scenarios

Suites go stale: the agent gets refined against the same dozen cases and the campaign stops
finding anything to fix. The scenario generator drives the `Evaluator.ScenarioGenerator`
prompt (pinned to a high-effort reasoning model) to author fresh, diverse evaluation
scenarios that probe a target category of behavior — including a verifiable ground truth
when the category allows one.

```bash
revi generate --agent FactChecker --category adversarial-sources --count 5 \
    --spec Docs/factchecker-spec.md
```

HTTP: `POST /api/refinery/generate-scenarios` with
`{ "agentName": "...", "agentSpecSection": "...", "targetCategory": "...", "count": 5 }`.

**Inputs.**

- `agent` — the agent the scenarios exercise.
- `--spec <file>` — a section of the agent's spec/definition sent as context so the
  generated cases probe real requirements rather than generic ones. The CLI sends the file's
  content; empty if omitted (the API requires a non-empty `agentSpecSection`).
- `--category` — the target category, freeform (e.g. `adversarial-sources`,
  `off-topic-redirect`, `contradictory-context`). This is the diversity lever: generate a
  batch per category rather than one big batch.
- `--count` — how many to ask for (default 5).

**Output.** A list of ready-to-use `Scenario` objects: `Id`, `Inputs` (named agent inputs),
`WorldSeed`, `Rubric`, `ExpectedInvariants`, `HeldOut`, `Tags`, `Notes`, and `GroundTruth`.
Candidates are deduplicated by a normalized fingerprint (agent name + sorted tags + sorted
input values — casing, ordering, and whitespace don't count as differences), so a batch
won't contain near-duplicates.

**Gotchas.** Generated scenarios are *returned*, not saved — review them and add the keepers
to the plugin's suite yourself (this is deliberate: a scenario with a wrong `GroundTruth`
poisons both the gate and calibration). And because the HTTP endpoint passes an empty
"existing scenarios" list to the generator, dedup only applies within the batch — check new
candidates against your current suite manually.

---

## Durable campaign history: Mongo vs in-memory

By default (`Forge:CampaignStore` = `"inmemory"` in
[appsettings.json](../appsettings.json)) campaigns and ledgers live in process memory: fine
for trying things out, but a restart wipes the history — and with it everything the
meta-analyzer had to mine.

To persist, set both of:

```jsonc
"Forge": { "CampaignStore": "mongo" },
"Observer": { "MongoDb": { "ConnectionString": "mongodb://localhost:27017" } }
```

The store reuses the Observer/ReviLog connection string and writes to a `refinery` database
with two collections: `refinery_campaigns` (keyed by campaign id) and `refinery_ledger`
(indexed by campaign id + round for ordered reads). Both conditions must hold — `mongo` mode
with no Observer connection string silently falls back to in-memory (Forge logs which store
won at startup).

Implementation note: the SDK records are immutable `required`/`init` records, which Mongo's
POCO serializer handles poorly, so each record is serialized to JSON and stored inside a
thin BSON envelope, with only the query keys (`_id`, `campaignId`, `round`) lifted to the
top level. Robust and drift-proof, but it means the payloads are opaque to ad-hoc Mongo
queries — use the API/CLI to read campaign history, not `mongosh`.
