# `.agent` Agent Orchestration Files

`.agent` files define state-machine style agent loops in ReviDotNet. They live under `RConfigs/Agents` and are loaded by `AgentManager`.

## File Location and Name Resolution

- Runtime lookup path: `RConfigs/Agents/**/*.agent`
- Effective agent name: the declared `[[information]] name`, verbatim.

Example:
- File: `RConfigs/Agents/Research/market-scan.agent`
- `[[information]] name = market-scan`
- Effective name: `market-scan`

Use this exact effective name in code:

```csharp
using Revi;

AgentResult result = await Agent.Run("market-scan");
string? output = await Agent.ToString("market-scan", "Find recent pricing trends.");
```

## Sections

### `[[information]]` (Required)

| Option | Type | Description |
| :--- | :--- | :--- |
| `name` | string | Declared logical agent name; folders do not add a prefix. |
| `version` | integer | Optional version number. Must be an integer if present — a non-integer value throws a `FormatException` that is caught per-file and **skips the entire agent**, so the agent silently won't be found at runtime. |
| `description` | string | Optional description for maintainers. |

> **Load-time vs run-time errors:** a malformed `.agent` file (e.g. a non-integer `version`) is skipped during loading with a logged warning rather than throwing to your startup. Structural problems such as a missing `[[loop]] entry` or an entry/transition that names a state which doesn't exist are also logged at load and then surface as a **run-time** failure when you invoke the agent — not as a load-time rejection. Watch the startup logs when an agent "isn't found" or fails immediately.

### `[[loop]]` (Required)

| Option | Type | Description |
| :--- | :--- | :--- |
| `entry` | string | Entry state name. Must exist in `[[state.<name>]]`. |

### `[[settings]]` (Optional)

Run-wide configuration applied across every state in the run.

| Option | Type | Description |
| :--- | :--- | :--- |
| `cost-budget` | decimal | Optional run-wide USD cost budget. The runner accumulates the cost of every LLM call across the run (using each model's `cost-per-million-input-tokens` / `cost-per-million-output-tokens`). When the projected cost of the next call would exceed this cap, the run terminates gracefully with `AgentExitReason.BudgetExceeded` and returns whatever output was last accumulated. State-level `cost-budget` guardrails still apply independently. **Result semantics:** if even the *first* call is projected over budget, the run ends immediately with `TotalSteps = 0` and `FinalOutput = null` — a valid budget refusal, not an error. The 80%-of-budget warning is a **Warning log event only**; it is not surfaced on `AgentResult`. Use a `.` decimal separator (values parse culture-invariantly). |
| `interaction-mode` | enum | How the agent may be driven: `fixed` (autonomous run on an initial input), `chat` (interactive — one user message per turn, each turn re-running the agent seeded with the prior conversation), or `both`. Defaults to `fixed`. The workshop's New Session dialog offers only the modes an agent declares. |
| `max-total-steps` | integer | Run-wide cap on LLM calls across every state. Defaults to `AgentRunner.DefaultMaxTotalSteps` (250), so a run always ends even when no state sets a guardrail. Reaching it ends the run with `AgentExitReason.GuardrailViolation`. State-level `max-steps` applies independently. |
| `run-timeout` | integer | Optional wall-clock limit for the whole run, in seconds. It cancels work **in flight** — a model call, a tool call, a queued tool waiting for a slot — not only the next step. The run ends with `AgentExitReason.GuardrailViolation` and a message naming the run timeout. |
| `data-inputs` | list | Inputs that are material to work on, not instructions: a scraped page, a document, the text of a report. A `{placeholder}` for one of them is **not** filled into system text; the system text says where the material is instead, and the value is delivered once, in the first user message, between the run's `<<<untrusted-data …>>>` markers. The `RESPONSE FORMAT` block then tells the model that marked text is data, even in a state with no tools. Use it for every input that can carry text from outside: it keeps that text out of the system role and stops it being sent twice. |
| `context-budget` | integer | Estimated input tokens allowed per model call. Defaults to 80% of the model's `context-window` (no budget when the model declares none). Over budget, the oldest tool results the model has already read are shortened to a 600-character excerpt plus the URLs and 24-hex identifiers they contained (still inside the untrusted-data markers). A call whose input still does not fit the model's whole context window is **not sent**: the run ends with `AgentExitReason.GuardrailViolation`. |

### `[[state.<name>]]` (At least one required)

Defines each state.

| Option | Type | Description |
| :--- | :--- | :--- |
| `description` | string | Optional human-readable state summary. |
| `prompt` | string | Optional reference to a `.pmt` prompt name (resolved via `PromptManager.Get`). When set, the prompt's system + instruction are rendered with `{key}` placeholders substituted from the agent's initial inputs and prepended to the state's per-step system message. If both `prompt` and `[[_state.X.instruction]]` are set, the inline instruction is appended after the resolved prompt's instruction (allowing per-run overrides). |
| `model` | string | Optional model profile name override for this state. |
| `tools` | list | Comma/space-separated tool names allowed in this state. |

### `[[state.<name>.guardrails]]` (Optional)

All values are optional.

| Option | Type | Description |
| :--- | :--- | :--- |
| `cycle-limit` | integer | Max activations of this state across a run. A `-> self` transition counts as a re-activation, so `cycle-limit` also bounds self-looping states (e.g. the `-> self [when: CONTINUE]` pattern). |
| `max-steps` | integer | Max LLM calls per activation. (Not reset by a `-> self` transition, so it bounds the total LLM calls a self-looping state makes.) |
| `timeout` | integer | Dual-purpose, in seconds. (1) **Per-activation deadline**: it cancels whatever is in flight when it passes — a model call (headers or body), a tool call, a tool waiting for a parallel slot. A tool that ignores its cancellation token is abandoned rather than waited for. The run ends with `AgentExitReason.GuardrailViolation` and a message naming the state timeout. (2) **Per-LLM-call inactivity timeout**: passed to the inference call as the no-data timeout. If the provider sends no headers, or the response body stops arriving, for this long, the attempt fails and is retried like any transient failure. |
| `cost-budget` | decimal | Optional USD cost budget for one activation of this state. Tracked alongside the run-wide `[[settings]] cost-budget`; the runner refuses an LLM call whose projected cost would exceed either cap. A warning event fires when consumption first crosses 80% of the cap. Models without `cost-per-million-*-tokens` rates contribute zero to tracking. |
| `tool-call-limit` | integer | Max tool calls per activation. Defaults to `AgentRunner.DefaultToolCallLimit` (32). Unlike the other guardrails, exceeding it does **not** terminate the run — excess tool calls beyond the limit are not run. Each one is answered in the conversation with a `[Tool: <name>] Error: Not run: …` message naming the limit, a `tool-dropped` event is emitted, and the state continues. |
| `max-parallel-tools` | integer | Max tool calls from a single step that may execute concurrently. Excess calls queue and start as slots free (each emits a `tool-call` event immediately, then a `tool-start` event once a slot is acquired). Defaults to `AgentRunner.DefaultMaxParallelTools` (4), so the model never decides alone how many calls start at once. |
| `retry-limit` | integer | Max retries of a failed LLM call within a single activation of this state before the run terminates with `AgentExitReason.Error`. Defaults to `0` (no retry). |
| `loop-detection` | boolean | Enables repeated-traversal loop detection for **this** state. Constraints: (1) detection only runs while a state that has it enabled is active — so to catch an `A <-> B` ping-pong you must set it on **both** A and B; (2) it fires when the same round of states has just run twice in a row with the same signals and the same tool calls (name and input) and the run is starting that round a third time, so it needs **≥ 5** history entries — a round that revisits the same states but does different work, such as a new search query, is not a loop; (3) `-> self` self-loops are **not** added to the traversal history and therefore can't be caught this way (bound them with `max-steps`/`timeout`/`cost-budget`/`cycle-limit` instead). |
| `max-agent-depth` | integer | Maximum sub-agent nesting depth permitted from this state. If `invoke_agent` would push depth above this, the call is refused. Defaults to `AgentRunner.DefaultMaxAgentDepth` (3). |

### `[[_system]]` (Optional)

Global system text applied to every step.

### `[[_state.<name>.instruction]]` (Optional)

State-specific instruction appended to the global system text.

### `[[_state.<name>.settings]]` (Optional)

Per-state inference overrides applied to every LLM call made while this state is active. Each line is a `key = value` pair (the same keys used in a `.pmt` `[[settings]]`/`[[tuning]]` block). A value set here overrides the resolved model-profile parameter for this state only.

Supported keys:

| Key | Type | Description |
| :--- | :--- | :--- |
| `output-budget` | integer | Max tokens to generate for calls in this state. |
| `best-of` | integer | Request multiple completions and keep the best. |
| `use-search-grounding` | boolean | Enable search grounding (if the model/provider supports it). |
| `temperature` | float | Sampling temperature. |
| `top-k` | integer | Top-K sampling. |
| `top-p` | float | Nucleus sampling. |
| `min-p` | float | Minimum-probability sampling threshold. |
| `presence-penalty` | float | Presence penalty. |
| `frequency-penalty` | float | Frequency penalty. |
| `repetition-penalty` | float | Repetition penalty. |
| `thinking` | string | Reasoning amount for calls in this state (`low`/`medium`/`high`, or `off`). Overrides the model's `thinking` default; when unset, the model's default applies. Either way the model's `thinking-conversion-*` table translates it — the same resolution a `.pmt` prompt call uses. |

`[[_system]]`, `[[_state.<name>.instruction]]` and a state's `prompt` are all templates: each `{placeholder}` is filled from the agent's inputs in a single pass, so a placeholder that appears *inside* an input's value is left exactly as written. An input whose placeholder every step's system text already fills is not repeated in the first user message.

```ini
[[_state.draft.settings]]
temperature = 0.2
output-budget = 800
```

### `[[_loop]]` (Required for transitions)

Raw loop DSL describing transitions between states.

## Loop DSL

Pattern:
- Non-indented line: state name declaration
- Indented `-> target [when: SIGNAL]`: conditional transition
- Indented `-> target`: fallback transition (no signal)
- Special target: `[end]` to finish successfully (`AgentExitReason.Completed`)
- Special target: `[abort]` to finish **without** success (`AgentExitReason.Aborted`) — the transition for "this cannot be done". The step's content is carried in `GuardrailViolationMessage`. Never point an `ABORT`/`ERROR` signal at `[end]`: that reports the failure to the caller as a completed run.
- Special target: `self` to stay in same state

Example:

```ini
[[_loop]]
search
  -> search [when: CONTINUE]
  -> summarize [when: READY]
  -> [abort] [when: ABORT]

summarize
  -> [end] [when: DONE]
  -> self
```

Notes:
- Transition matching checks `signal` first, then first unconditional transition.
- Signal tokens should be uppercase with underscores (for example `READY_FOR_SUMMARY`).
- **State names may contain letters, digits, and hyphens — but NOT underscores.** State discovery parses `[[state.<name>]]` headers up to the first `.` or `_`, so an underscore in a state name truncates it and the intended state is never registered. (The `[end]`, `[abort]` and `self` targets are reserved.)
- **Each state must declare at least one plain `[[state.<name>]]` field** (e.g. `description`, `model`, or `tools`). A state that only has `[[state.<name>.guardrails]]` or `[[_state.<name>.instruction]]` — with no plain `[[state.<name>]]` field — is never discovered, so referencing it from `entry`/a transition resolves to a non-existent state.

Either pitfall produces a state that doesn't exist with no load-time error; the failure surfaces at run time as an entry/transition error.

## Signal Validation

When the LLM emits a `signal` value that does not appear in the current state's loop transitions, the runner does not silently spin. Instead it:

1. Emits a `signal-unknown` error event under the current step.
2. Appends a corrective system note to the conversation history listing the valid signal set: `"Signal 'XXX' is not valid from state 'NAME'. Valid signals are: A, B, C. Re-emit your decision with one of these signals."`
3. Continues to the next step, where the LLM has a chance to retry with the corrected guidance.

Up to `MaxSignalCorrectionsPerActivation` (currently 2) corrections are absorbed per state activation. Beyond that the run terminates with `AgentExitReason.InvalidSignal` and a guardrail message naming the latest bad signal and the declared signal set. A null/empty `signal` (no decision yet) is not counted as a correction — it triggers the existing "stay in state" behaviour and the next step proceeds normally.

## Tool Registration

Built-in tools are registered statically in `ToolManager`'s static constructor: `web-search`, `web-scrape`, and `web-extract` are always available. `invoke_agent` is **not** registered statically — it requires `Lazy<IAgentService>` and is registered only on the DI path by `ToolManagerService`. So `invoke_agent` works when you run agents through the injected `IAgentService`/`ReviClient`, but is unavailable on the static `Agent.Run` path (a state that allows it there will get a "tool is not registered" result).

### Built-in tools

| Tool name | Input | Output |
| :--- | :--- | :--- |
| `web-search` | A search query string. | Search results. |
| `web-scrape` | A URL to fetch. | Page content. |
| `web-extract` | Either a bare URL string, or JSON `{ "url": "<url>", "maxTokens": <n> }` (the key `uri` is also accepted; `maxTokens` is clamped to **64–2000**, default **400**). | Structured JSON: the page's main content split into token-bounded chunks. |
| `invoke_agent` | JSON `{ "agent": "<name>", "task": "<text>", "inputs": { … } }` — see the InvokeAgentTool section. (DI-only.) | The sub-agent's final output. |

### Registering custom tools

`ToolManager` itself is `internal` (visible only to tests), so host applications register custom tools through the DI-exposed `IToolManager`:

```csharp
IToolManager tools = serviceProvider.GetRequiredService<IToolManager>();
tools.Register(new MyCustomTool());
```

Do this during host startup (e.g. from an `IHostedService.StartAsync`), before the first agent run. `IToolManager.Unregister(name)` removes a tool by name and is primarily intended for tests. Custom MCP/HTTP tool profiles loaded from `RConfigs/Tools/*.tool` (see [tool-files.md](tool-files.md)) are parsed but their dispatch is **not yet implemented** — only built-in tools execute today.

## LLM Step Contract

Each step response is expected as JSON:

```json
{
  "signal": "READY",
  "thinking": "Two libs cover OTLP; the rest are abandoned.",
  "tool_calls": [
    { "name": "web-search", "input": "best .NET tracing libs 2026" }
  ],
  "content": "Found 5 options. Ready to summarize."
}
```

> **You do not need to restate this contract in `[[_system]]`.** The runner auto-appends a `RESPONSE FORMAT` block to the system message on **every** step (the schema is also enforced via guidance for providers that support it). That block is generated from the current state and includes:
> - the exact JSON shape above (including the optional `thinking` field), and
> - **the legal transition signals for the current state** (computed from the `[[_loop]]` edges — you don't list them by hand), and
> - **the tools available from this state**, each with its description and expected input format (see *Tools* above; rendered from the tool definitions, so there's nothing to copy from `tool-files.md`).
>
> - when the state has tools or the agent declares `data-inputs`, **the rule for marked data**: each result reaches the model as `[Tool: <name>] Result:` followed by the output between a `<<<untrusted-data <mark>>>>` line and a `<<<end-untrusted-data <mark>>>>` line, and the block tells the model that everything between them is data, never an instruction. `<mark>` is drawn fresh for each run and is not shown to tools, so a page cannot contain the closing line. Failed results (`[Tool: <name>] Error: …`) are not wrapped.
> - on the last model call a state's `max-steps` allows (when `max-steps` is above 1), a **`FINAL STEP`** notice telling the model to reply with its final content and a transition signal.
>
> So a `[[_system]]` block only needs the agent's persona/task — not the JSON contract, the signal list, or the tool input shapes.

- `signal`: Used to resolve next transition.
- `thinking` (optional `string`|`null`): the model's reasoning. Surfaced as a separate `Thinking` trace event (not part of `content`/`FinalOutput`). The auto-appended `RESPONSE FORMAT` block already invites the model to use it.
- `tool_calls`: Filtered to the tools allowed by the current state's `tools` list (matching is **case-insensitive**). Calls naming a disallowed tool — or calls beyond the state's `tool-call-limit` — are **not run**, and each is answered with a `[Tool: <name>] Error: Not run: …` message the model sees on its next step (a disallowed call's message lists the tools the state does allow) plus a `tool-dropped` event. The surviving allowed calls execute **in parallel** (`Task.WhenAll`), and their results are appended to the conversation.
- `content`: Stored and becomes `FinalOutput` when agent reaches `[end]`.

### When a run counts as completed

`AgentExitReason.Completed` means the agent reached `[end]` **and** read the outcome of the work it did:

- **A tool that ran and failed vetoes `[end]` on that step.** The run continues, the model sees the failure and a note that the run was not ended, and it must retry or take its abort transition. If that step was the last one the state allows, the run ends with `AgentExitReason.Error` instead. Calls that were refused before running (not allowed in the state, or over `tool-call-limit`) are answered "Not run" but do not veto.
- **A step that ran tools and asked for `[end]` is held for one more step**, so the model reads the results before the run ends — unless that step was the state's last allowed one. A confirming reply with empty `content` keeps the content the run was about to end on.
- **An unfinished provider reply is never accepted**, however well-formed its JSON. A finish reason of `length`/`max_tokens`/`max_output_tokens`, a content-filter or refusal stop, or a detected repetition loop discards the reply and asks again, up to `MaxSignalCorrectionsPerActivation` times per activation; after that the run ends with `AgentExitReason.Error`.

`AgentResult.FinalSignal` carries the signal of the step that ended the run, and `AgentResult.FailedToolCalls` counts every failed or refused tool call across the run.

## Complete Example

```ini
[[information]]
name = market-scan
version = 1
description = Researches a topic and returns a short summary.

[[loop]]
entry = search

[[state.search]]
description = Gather source material
model = gpt4o_mini
tools = web-search web-scrape

[[state.search.guardrails]]
cycle-limit = 3
max-steps = 8
timeout = 60
tool-call-limit = 4
loop-detection = true

[[state.summarize]]
description = Turn findings into final answer
model = gpt4o_mini
tools = 

[[state.summarize.guardrails]]
max-steps = 3
timeout = 30
loop-detection = true

[[_system]]
You are a concise research assistant.
Return grounded answers and avoid speculation.

[[_state.search.instruction]]
Search for evidence and call tools when needed.
When enough evidence is collected, emit signal READY.

[[_state.summarize.instruction]]
Summarize the evidence in 5 bullets and emit DONE.

[[_loop]]
search
  -> search [when: CONTINUE]
  -> summarize [when: READY]
  -> [abort] [when: ABORT]
summarize
  -> [end] [when: DONE]
  -> summarize [when: CONTINUE]
```

## Runtime and DI Notes

- `RegistryInitService` loads agents via `AgentManager.Load(...)` at startup.
- You can access loaded agents through:
1. static API: `AgentManager.Get(...)`
2. DI facade: `IAgentManager` (implemented by `AgentManagerService`)

## Analyzer Setup for `.agent` Files

Include agent files as AdditionalFiles so agent analyzers can validate names and duplicates:

```xml
<Project>
  <ItemGroup>
    <AdditionalFiles Include="RConfigs\Agents\**\*.agent" />
  </ItemGroup>
</Project>
```

Related analyzer rules:
- `REVI006`: referenced agent name not found
- `REVI007`: duplicate effective agent names
- `REVI008`: non-constant agent name in `Agent.Run` / `Agent.ToString` / `Agent.FindAgent`
- `REVI011`: agent state-graph problems — an underscore in a state name (undiscoverable), a loop node / transition target / entry with no `[[state.*]]` definition, a dead edge after an unconditional fallback, or a duplicate signal. The runtime also logs these as warnings at load time.
