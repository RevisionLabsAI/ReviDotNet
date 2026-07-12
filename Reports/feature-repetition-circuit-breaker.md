# Feature proposal: repetition circuit-breaker for model output

**Status:** P1 IMPLEMENTED (2026-07-13) — `RepetitionDetector` (`repeat-N` suffix-period algorithm),
model config `[[settings]] loop-detection = repeat-512`, post-hoc `FinishReason = "repetition"`
classification in `InferService`/`Infer`/`AgentRunner`, mid-stream cancellation in
`CompletionStream`; default OFF everywhere; 18 unit tests incl. false-positive corpus. P2 (retry
escalation) and P3 (Refinery invariant, Forge UI) remain open. · **Date:** 2026-07-13 · **Requested by:** Bryan

## Problem

Language models — small and locally-hosted ones especially — can degenerate into repetition loops:
emitting the same token run, line, or paragraph over and over until they hit the output-token
ceiling. Nothing in ReviDotNet stops this today; a looping model burns its **entire** `max_tokens`
budget producing garbage, and the caller pays for every token (and waits for it — output length is
latency).

Two recent changes make this more relevant, not less:

1. We just **raised output ceilings** across the board (removed forced model-level `max-tokens`
   overrides, 8192 on chat answers, 8000 on the Refinery proposer). Generous ceilings are correct —
   `max_tokens` is a stop ceiling, not a billed amount — *provided* nothing pathological runs to the
   ceiling. A repetition breaker is the guard that makes generous ceilings safe on every model tier.
2. The Refinery now treats a `max_tokens`/`length` finish as a hard invariant failure (CC-2). A
   repetition loop is the most likely *cause* of an unexpected ceiling hit on small models, and today
   it is only detected after the full budget is spent.

Existing mitigations and why they don't cover this:

| Mechanism | Covers | Gap |
| :--- | :--- | :--- |
| `repetition-penalty` / `frequency-penalty` tuning | discourages repetition statistically | not supported by every provider (Gemini ignores repetition-penalty; Anthropic has neither); doesn't *stop* a loop once entered |
| `inactivity-timeout` (`InferClient`) | stalled streams | a looping model is *not* stalled — it streams happily |
| `max_tokens` ceiling | bounds the damage | the damage is the whole ceiling; with 8–32k ceilings that is real money and latency |
| `AgentRunner` loop-detection guardrail | state-machine loops across steps | not token-level repetition within one completion |

## Proposal

Detect degenerate repetition in the accumulating output text and **cancel the generation
mid-stream**, returning the partial result with an explicit finish reason.

### Where it hooks in

The framework already has the exact seam this needs — `ToStringListLimited`
(`InferService.cs`) watches a stream chunk-by-chunk and cancels early via a linked
`CancellationTokenSource` when an evaluator says stop. The breaker generalizes that pattern:

1. **Streaming path (the real win).** `InferClient.GenerateStreamAsync` / `CompletionStream`:
   a detector observes the accumulated tail as chunks arrive; on trip, cancel the linked CTS.
   For streamed requests, cancellation stops the provider from generating further tokens — this is
   where cost and latency are actually saved. Applies to all protocols including vLLM/LLamaAPI
   local models, the most loop-prone tier.
2. **Non-streaming path (classification only).** A single-response completion cannot be stopped
   mid-flight. Run the same detector over the finished text and, on trip, stamp the result
   (see finish reason below) so callers and traces see "this output is degenerate" instead of
   treating it as a normal answer. Optionally feeds the retry policy (below).
3. **Trace visibility for free.** `AgentRunner.BuildCompletionMeta` now stamps `finishReason` into
   every `llm-response` trace event (added for CC-2). A breaker that sets
   `FinishReason = "repetition"` is therefore immediately visible to Refinery invariant checkers —
   a future CC-3 "no degenerate repetition" checker is a ~20-line plugin addition, and the
   detector itself can also be re-run deterministically over `FinalOutput` (no LLM cost).

### Detection algorithm

Two cheap, high-precision detectors run over a sliding tail window (default ~2,000 chars),
re-evaluated every K chars (default 256) rather than per chunk, so the cost is amortized O(1):

1. **Periodic-tail detector (exact loops).** Compute the smallest period `p` of the window's tail
   using the KMP failure function. Trip when the tail consists of ≥ `min-repeats` (default 4)
   consecutive repeats of a unit of length ≤ `max-unit-length` (default 400 chars) AND the repeated
   region is ≥ `min-loop-length` (default 600 chars). Catches token-level and sentence-level exact
   loops ("the answer is the answer is the answer is …").
2. **Line-cycle detector (list loops).** Hash trailing lines; trip when the same line (or a cycle of
   ≤ C distinct lines, default 3) repeats ≥ R times consecutively (default 6). Catches the classic
   small-model failure of emitting the same bullet/JSON row forever, which the periodic detector can
   miss when separators vary slightly.

Deliberately **not** in v1: fuzzy/n-gram novelty-ratio detection ("the last 500 tokens have low
distinct-n-gram ratio"). It catches paraphrase loops but is exactly where false positives live.
Ship it later as an opt-in `aggressive` mode if exact detection proves insufficient.

**False-positive guardrails (the design's hard part).** Legitimate outputs repeat: tables, JSON
arrays of similar objects, code boilerplate, refrains, "| --- | --- |" rows. Defaults must be
conservative:

- Never trip before `min-output-chars` (default 512) have been generated.
- Repeat thresholds high enough that structured-but-legit output survives (a 27-row table repeats a
  *pattern* but not an identical 400-char unit 4+ times).
- The unit-comparison is exact-match on the normalized tail (whitespace-collapsed), not fuzzy.
- When `request-json = true`, consider raising thresholds further (JSON is repetitive by nature) —
  open question below.

### Behavior on trip

1. Cancel the stream (streaming) / classify (non-streaming).
2. Return the partial `CompletionResult` with a new `FinishReason = "repetition"` value — callers
   that check `max_tokens`/`length` today get a distinct, truthful signal; the Refinery trace picks
   it up automatically.
3. Log one structured warning (`Util.Log`) with the detected unit and repeat count — the evidence
   string a future CC-3 checker would quote.
4. **Optional retry escalation** (phase 2): when the prompt has `retry-attempts` remaining, retry
   with bumped sampling (temperature +0.2, and `repetition-penalty`/`frequency-penalty` where the
   provider supports them). Mirrors the existing JSON-retry pattern in `ToObjectCore`.

### Configuration surface

Follow the rcfg conventions, and learn from the `max-tokens` lesson — define precedence explicitly
and document whether each level is a default or a force:

```ini
# model .rcfg — [[circuit-breaker]] (new section); all optional
[[circuit-breaker]]
repetition-break = true        # default FALSE for tier-A cloud models; recommend true for local/small
repetition-window = 2000       # chars of tail examined
repetition-min-repeats = 4     # unit repeats required to trip
repetition-min-length = 600    # minimum total looped chars
```

- **Model-level value is the default; a prompt-level `[[settings]] repetition-break` override wins**
  (the opposite of `max-tokens` — documented in prompt-files.md/model-files.md from day one, with a
  test pinning the precedence).
- `enabled = false` everywhere by default in v1: zero behavior change until a config opts in.
  GreatDebate would opt in nothing initially; the natural first users are local vLLM models.

### What this deliberately does not do

- No mid-stream *content* rewriting or salvage — the output is returned as-is up to the trip point.
- No attempt to detect semantic rambling (that is the Refinery conciseness facet's job — a judge
  problem, not a circuit-breaker problem).
- No cross-call memory: each completion is scored independently.

## Phasing & effort

| Phase | Scope | Est. size |
| :--- | :--- | :--- |
| P1 | `RepetitionDetector` (pure, ~150 LOC) + unit tests (loop corpus + false-positive corpus: tables/JSON/code/lists) + streaming hook + `FinishReason = "repetition"` + logging | small |
| P2 | rcfg config surface + precedence tests + non-streaming classification + retry escalation | small-medium |
| P3 | Refinery CC-3 invariant (plugin-side), Forge model-edit UI fields | small |

The detector is pure and independently testable; nothing touches provider payloads, so regression
risk concentrates in the streaming cancellation path — which already has the `ToStringListLimited`
precedent to copy.

## Open questions for review

1. **JSON outputs:** raise thresholds (or disable line-cycle) when `request-json = true`, or trust
   the exact-match conservatism? Leaning: trust v1 defaults, revisit with corpus evidence.
2. **Thinking blocks:** Claude/Gemini reasoning streams can legitimately iterate on a phrase while
   thinking. Detector should probably only observe *answer* text where the protocol distinguishes
   them; where it can't, the `min-output-chars` floor is the guard. Needs a look at how thinking
   chunks flow through `GenerateStreamAsync` per provider.
3. **Token-vs-char basis:** chunks arrive as text; chars are the simple, provider-agnostic basis.
   Any reason to prefer token counts? (Leaning no — thresholds are heuristic anyway.)
4. **Default-on for which models?** Proposal: ship default-off everywhere; flip on for vLLM/LLamaAPI
   protocol configs in the sample rcfgs only.
