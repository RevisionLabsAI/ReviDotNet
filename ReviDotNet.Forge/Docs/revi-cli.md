# revi — the Refinery CLI

Source: [Program.cs](../../ReviDotNet.Cli/Program.cs),
[RefineryClient.cs](../../ReviDotNet.Cli/RefineryClient.cs)

`revi` is a thin command-line client over the Forge Refinery Control API (`/api/refinery/*`).
Everything it does, the [/refinery dashboard](features.md#refinery-refinery) can also do —
the CLI exists so campaigns, suite runs, and calibration checks can be driven from a
terminal or a CI pipeline. It holds no state of its own: every command is one or more
HTTP calls against a running Forge instance.

- Campaign concepts (baselines, rounds, variants, ledger, promotion) are covered in
  [refinery-campaigns.md](refinery-campaigns.md).
- Writing the plugin that gives Forge agents/suites to refine is covered in
  [refinery-plugin-authoring.md](refinery-plugin-authoring.md).

---

## Building and running

The CLI lives in the `ReviDotNet.Cli` project. During development, run it through
`dotnet run` (note the `--` separating dotnet's arguments from revi's):

```
dotnet run --project ReviDotNet.Cli -- plugins list
```

For day-to-day use, publish it once and put the binary on your PATH:

```
dotnet publish ReviDotNet.Cli -c Release -o ./tools
./tools/revi plugins list
```

Forge must be running and reachable — the CLI does not start it for you. If it is not,
every command fails fast with a connection error and exit code 2.

---

## Global flags

These are extracted before command dispatch, so they can appear anywhere on the line:

| Flag | Meaning |
| --- | --- |
| `--url <baseUrl>` | Forge base URL. Overrides the `FORGE_URL` environment variable. Default: `http://localhost:5000` (the Kestrel dev default). |
| `--json` | Emit raw JSON instead of formatted tables. Exactly **one** JSON document is written to stdout — nothing else — so output can be piped straight into `jq` or a file. |
| `--help` / `-h` | Print the built-in help text. Running `revi` with no arguments does the same. |

Resolution order for the base URL: `--url` flag → `FORGE_URL` environment variable →
`http://localhost:5000`.

If Forge is started with `Forge:RefineryApi:RequireApiKey = true`, the Control API is
gated behind the same `X-Forge-ApiKey` check as the inference gateway. The CLI does not
currently send that header — point it at an ungated (local) instance, or leave the gate
off where the CLI runs.

---

## Command reference

### `revi plugins list`

`GET /api/refinery/plugins`. Lists every loaded refinement plugin with its agent, suite,
and invariant counts. Load errors and warnings print indented under the affected row.

```
$ revi plugins list
NAME                           STATUS       AGENTS SUITES   INV
----------------------------------------------------------------
GreatDebate.Refinery           Loaded            2      3     5
```

### `revi plugins refresh`

`POST /api/refinery/plugins/refresh`. Rebuilds and reloads **all** configured plugin
repos, then prints the updated catalog (same table as `list`).

```
$ revi plugins refresh
Refreshing all plugins…
Refresh complete.
NAME                           STATUS       AGENTS SUITES   INV
----------------------------------------------------------------
GreatDebate.Refinery           Loaded            2      3     5
```

### `revi plugins reload <name>`

`POST /api/refinery/plugins/{name}/reload`. Hot-reloads a single plugin by name and
prints its refreshed row. An unknown name is a 404 from the server → exit code 2.

```
$ revi plugins reload GreatDebate.Refinery
Reloading plugin 'GreatDebate.Refinery'…
Reload complete.
GreatDebate.Refinery           Loaded            2      3     5
```

### `revi refine run`

`POST /api/refinery/campaigns`, then watch. Starts a refinement campaign and polls
`GET /campaigns/{id}` every 3 seconds (30-minute watch timeout) until the campaign
reaches a terminal status (`Converged`, `Failed`, `Stopped`, or `BudgetExhausted`),
then prints a campaign summary and a refinement-loop summary.

```
revi refine run --plugin <p> --agent <a> --suite <s>
                [--samples N] [--budget N] [--max-rounds N]
                [--mode live|replay] [--baseline-only]
```

| Flag | Required | Default | Meaning |
| --- | --- | --- | --- |
| `--plugin <p>` | yes | — | Plugin that owns the agent and suite. |
| `--agent <a>` | yes | — | Agent to refine. |
| `--suite <s>` | yes | — | Scenario suite to evaluate against. |
| `--samples N` | no | 3 | Samples per scenario. |
| `--budget N` | no | no limit | Token budget for the whole campaign. |
| `--max-rounds N` | no | 10 | Maximum improvement rounds. |
| `--mode` | no | `live` | `live` or `replay` (deterministic replay — see CI usage below). |
| `--baseline-only` | no | off | Measure the baseline only (`AutoPropose = false`); no variants are proposed. |

By default the full refinement loop runs: each round the engine proposes variants,
evaluates them on held-out scenarios, and accepts or rejects them against the regression
gate.

```
$ revi refine run --plugin GreatDebate.Refinery --agent Researcher --suite core --samples 3
Campaign started  id=1f3a…  status=Pending  mode=full refinement loop
Polling http://localhost:5000/ every 3 s (timeout 30 min)…
  status=Running              tokens=    412 903  rounds=  4

Campaign  : 1f3a…
Status    : Converged
Plugin    : GreatDebate.Refinery
Agent     : Researcher
Suite     : core
Tokens    : 812,455
Rounds    : 6
  Baseline:
    Inv pass-rate : 83.3%  (gated runs: 12/12)
    Quality mean  : 6.90  p10=5.80
    Cost mean     : $0.0412  latency p90=8213 ms
  Current :
    Inv pass-rate : 100.0%  (gated runs: 12/12)
    Quality mean  : 7.85  p10=7.10
    Cost mean     : $0.0398  latency p90=7904 ms

--- Refinement loop summary ---
  Rounds run        : 6
  Variants proposed : 11
  Variants accepted : 2
  Quality p10       : baseline=5.80  final=7.10  (+1.30)
  Inv pass-rate     : baseline=83.3%  final=100.0%  (+16.7%)
```

Exit code is 2 if the campaign ends in `Failed`, otherwise 0.

**Ctrl-C interrupts the watch only.** The campaign keeps running server-side — pressing
Ctrl-C during the poll loop detaches the CLI and prints a parting message telling you how
to pick it back up:

```
Watch interrupted — the campaign keeps running on the server. Check it with
'revi refine status <id>' or cancel it with 'revi refine stop <id>'.
```

To actually cancel the campaign, use `revi refine stop <id>`.

With `--json`, the CLI polls quietly and emits exactly one JSON document: the final
`Campaign` object, once a terminal status is reached.

### `revi refine status <id>`

`GET /api/refinery/campaigns/{id}`. Prints the campaign summary block (status, plugin,
agent, suite, tokens, rounds, baseline vs. current aggregates) for an existing campaign
— running or finished. Useful after detaching from a watch with Ctrl-C.

```
$ revi refine status 1f3a…
Campaign  : 1f3a…
Status    : Running
Plugin    : GreatDebate.Refinery
Agent     : Researcher
Suite     : core
Tokens    : 412,903
Rounds    : 4
  Baseline:
    Inv pass-rate : 83.3%  (gated runs: 12/12)
    Quality mean  : 6.90  p10=5.80
    Cost mean     : $0.0412  latency p90=8213 ms
  Current : (not yet available)
```

### `revi refine list`

`GET /api/refinery/campaigns`. One row per campaign, newest state included.

```
$ revi refine list
ID                                     STATUS           PLUGIN               AGENT                    TOKENS
------------------------------------------------------------------------------------------------------------
1f3a…                                  Converged        GreatDebate.Refinery Researcher               812455
9c02…                                  Running          GreatDebate.Refinery FactChecker              120884
```

### `revi refine ledger <id>`

`GET /api/refinery/campaigns/{id}/ledger`. Prints every accept/reject decision the
campaign made: round, knob type, accepted flag, held-out quality mean, invariant
pass-rate, tokens spent, and the reject reason (truncated to keep the table readable —
use `--json` for the full text).

```
$ revi refine ledger 1f3a…
 RND  KNOB                ACC  QUAL-MEAN  INV-RATE      TOKENS  REJECT REASON
------------------------------------------------------------------------------------------
   1  SystemPrompt        YES       7.20     91.7%       94211
   2  Sampling             no       6.10     83.3%       61780  held-out quality regressed below gate
   3  SystemPrompt        YES       7.85    100.0%       88342
```

### `revi refine stop <id>`

`POST /api/refinery/campaigns/{id}/stop`. Requests cancellation of a queued or running
campaign. Stopping is asynchronous — the server signals the campaign and it lands in
`Stopped` status shortly after; verify with `refine status`.

```
$ revi refine stop 1f3a…
Stop requested for campaign 1f3a… — it will land in Stopped shortly. Check with: revi refine status 1f3a…
```

Error cases (both exit code 2): unknown id → HTTP 404; campaign already in a terminal
state → HTTP 400 with the current status in the error message. With `--json` a
successful stop prints `{"stopped":true}`.

### `revi optimize <promptName>`

`POST /api/refinery/optimize`. The HTTP counterpart to the [Optimizer](features.md#optimizer-optimize)
page's analyze → suggest → revise loop, in one shot: runs the prompt against the given
models, analyzes each result, aggregates the analyses into suggestions, and returns the
fully revised `.pmt` content.

```
revi optimize <promptName> [--models a,b,...] [--runs N] [--suggestions K] [--save <path>]
```

| Flag | Meaning |
| --- | --- |
| `--models a,b` | Comma-separated model names to run against. |
| `--runs N` | Runs per model. |
| `--suggestions K` | Cap the number of suggestions applied. |
| `--save <path>` | Also write the revised prompt content to a file. |

```
$ revi optimize Search.AnalyzeSpecs --models anth_sonnet_45,gpt5_mini --runs 3 --save revised.pmt
Suggestions (3):
  [1] [instruction] Pin the output to exactly three bullet points; runs varied 2-5.
       impact: More consistent downstream parsing
  [2] [system] State the target audience explicitly.
       impact: Less generic phrasing
  [3] [instruction] Move the length constraint above the examples.
       impact: Higher instruction adherence

Revised prompt:
[[information]]
name = AnalyzeSpecs
…

Revised prompt saved to: revised.pmt
```

### `revi test <suiteName>`

`POST /api/refinery/test/run`. Runs a saved suite by name — in prompt-mode by default,
or agent-mode when `--agent <name>` is given — and prints a per-case pass/fail table with
failed assertions spelled out, followed by an aggregate line.

```
$ revi test smoke --agent Researcher
Suite : smoke  mode=agent

     #  PASS  OUTPUT / FAILURES
  ------------------------------------------------------------
     1  PASS  The capital of France is Paris. Sources: [1] …
     2  FAIL  I could not find a reliable source for that …
              ASSERTION cites-source FAILED: no citation marker found in output

Result: 1/2 passed
```

**Exit code 0 if all cases pass, 1 if any fail** — wire it directly into CI. This holds
with `--json` too: the raw `SuiteRunSummary` is printed, and the exit code still reflects
`passed == total`.

### `revi calibrate --agent <name> [--version <v>]`

`GET /api/refinery/calibration?agent=<name>[&version=<v>]`. Prints the confidence-vs-accuracy
reliability table for a fact-checker-style agent, its Expected Calibration Error (ECE),
and whether accuracy rises monotonically with confidence.

```
$ revi calibrate --agent FactChecker
Calibration report: FactChecker
  Runs (with truth): 148   Correct: 121

  CONFIDENCE    RUNS  CORRECT  ACCURACY    W-ERROR
  ----------------------------------------------------
           1      12        5     41.7%     0.0201
           2      31       19     61.3%     0.0187
           3      54       46     85.2%     0.0093
           4      51       51    100.0%     0.0000

  ECE              : 0.0481
  Monotonic        : yes
```

### `revi generate --agent <name> --category <cat> [--count N] [--spec <file>]`

`POST /api/refinery/generate-scenarios`. Asks the LLM scenario generator to author fresh
evaluation scenarios for the given agent in a target category (server default: 5 when
`--count` is omitted). `--spec <file>` sends the file's content as the agent-spec
section that grounds the generator; without it an empty string is sent.

```
$ revi generate --agent Researcher --category edge-cases --count 2 --spec researcher-spec.md
Generated 2 scenario(s) for agent 'Researcher' / category 'edge-cases':

  id    : edge-contradictory-sources
  tags  : edge-cases, sources
  notes : Two authoritative sources disagree; the agent must surface both.
  truth : Output flags the contradiction rather than picking a side.

  id    : edge-empty-results
  tags  : edge-cases, no-data
  …
```

Missing `--spec` file is a usage error (exit 1); the file is checked before any HTTP call.

---

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success — including "all test cases passed" for `revi test`, and a campaign that ended in any non-`Failed` terminal state for `refine run`. |
| `1` | Usage error (unknown command, missing required flag, missing `--spec` file) — help text is printed to stderr. Also: `revi test` when any case failed. |
| `2` | HTTP error (non-2xx from Forge, e.g. 404 unknown campaign, 400 already-terminal stop) or connection failure. Also: `refine run` whose campaign ended `Failed`. |

## CI usage notes

- **`--json` emits exactly one JSON document on stdout.** Progress and error text go to
  stderr, so `revi refine run --json … > campaign.json` is safe. For `refine run` the
  document is the final `Campaign`; the CLI polls silently until terminal.
- **`revi test` is the CI gate.** Exit code 1 on any failing case (with or without
  `--json`), so a plain `revi test <suite> --agent <agent>` step fails the build when
  the agent regresses.
- **Use `--mode replay` for deterministic CI campaigns** — replay mode re-scores recorded
  runs instead of paying for live inference, so results are reproducible and free.
- Set `FORGE_URL` in the pipeline environment instead of repeating `--url` on every step.
- Connection failures are exit code 2 with a hint on stderr (`make sure Forge is running
  and pass --url if using a non-default address`), so a missing Forge instance fails the
  job rather than hanging.
