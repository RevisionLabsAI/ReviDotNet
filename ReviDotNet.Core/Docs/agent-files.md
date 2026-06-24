# `.agent` Agent Orchestration Files

`.agent` files define state-machine style agent loops in ReviDotNet. They live under `RConfigs/Agents` and are loaded by `AgentManager`.

## File Location and Name Resolution

- Runtime lookup path: `RConfigs/Agents/**/*.agent`
- Effective agent name:
1. lower-cased subfolder path under `RConfigs/Agents/` (if any), plus
2. `[[information]] name`

Example:
- File: `RConfigs/Agents/Research/market-scan.agent`
- `[[information]] name = market-scan`
- Effective name: `research/market-scan`

Use this exact effective name in code:

```csharp
using Revi;

AgentResult result = await Agent.Run("research/market-scan");
string? output = await Agent.ToString("research/market-scan", "Find recent pricing trends.");
```

## Sections

### `[[information]]` (Required)

| Option | Type | Description |
| :--- | :--- | :--- |
| `name` | string | Logical agent name (combined with folder prefix). |
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
| `timeout` | integer | Dual-purpose, in seconds. (1) **Per-activation wall-clock**: checked at the top of the loop before each LLM call — a single long-running/streaming call is therefore not hard-capped mid-flight; the overrun is caught before the *next* call and surfaces as `AgentExitReason.GuardrailViolation`. (2) **Per-LLM-call inactivity timeout**: passed to the inference call as the no-data timeout — if the provider sends no data for this long the call aborts, surfacing as `Cancelled`/`Error` (not `GuardrailViolation`). |
| `cost-budget` | decimal | Optional USD cost budget for one activation of this state. Tracked alongside the run-wide `[[settings]] cost-budget`; the runner refuses an LLM call whose projected cost would exceed either cap. A warning event fires when consumption first crosses 80% of the cap. Models without `cost-per-million-*-tokens` rates contribute zero to tracking. |
| `tool-call-limit` | integer | Max tool calls per activation. Unlike the other guardrails, exceeding it does **not** terminate the run — excess tool calls beyond the limit are silently **dropped** (logged via `Util.Log` only, no event) and the state continues. |
| `max-parallel-tools` | integer | Max tool calls from a single step that may execute concurrently. Excess calls queue and start as slots free (each emits a `tool-call` event immediately, then a `tool-start` event once a slot is acquired). Defaults to unbounded (every tool call in the step runs at once). |
| `retry-limit` | integer | Max retries of a failed LLM call within a single activation of this state before the run terminates with `AgentExitReason.Error`. Defaults to `0` (no retry). |
| `loop-detection` | boolean | Enables repeated-traversal loop detection for **this** state. Constraints: (1) detection only runs while a state that has it enabled is active — so to catch an `A <-> B` ping-pong you must set it on **both** A and B; (2) it detects repeating multi-state sub-sequences in the traversal history and needs **≥ 4** history entries to fire; (3) `-> self` self-loops are **not** added to the traversal history and therefore can't be caught this way (bound them with `max-steps`/`timeout`/`cost-budget`/`cycle-limit` instead). |
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
| `max-tokens` | integer | Max tokens to generate for calls in this state. |
| `best-of` | integer | Request multiple completions and keep the best. |
| `use-search-grounding` | boolean | Enable search grounding (if the model/provider supports it). |
| `temperature` | float | Sampling temperature. |
| `top-k` | integer | Top-K sampling. |
| `top-p` | float | Nucleus sampling. |
| `min-p` | float | Minimum-probability sampling threshold. |
| `presence-penalty` | float | Presence penalty. |
| `frequency-penalty` | float | Frequency penalty. |
| `repetition-penalty` | float | Repetition penalty. |

```ini
[[_state.draft.settings]]
temperature = 0.2
max-tokens = 800
```

### `[[_loop]]` (Required for transitions)

Raw loop DSL describing transitions between states.

## Loop DSL

Pattern:
- Non-indented line: state name declaration
- Indented `-> target [when: SIGNAL]`: conditional transition
- Indented `-> target`: fallback transition (no signal)
- Special target: `[end]` to finish
- Special target: `self` to stay in same state

Example:

```ini
[[_loop]]
search
  -> search [when: CONTINUE]
  -> summarize [when: READY]
  -> [end] [when: ABORT]

summarize
  -> [end] [when: DONE]
  -> self
```

Notes:
- Transition matching checks `signal` first, then first unconditional transition.
- Signal tokens should be uppercase with underscores (for example `READY_FOR_SUMMARY`).
- **State names may contain letters, digits, and hyphens — but NOT underscores.** State discovery parses `[[state.<name>]]` headers up to the first `.` or `_`, so an underscore in a state name truncates it and the intended state is never registered. (The `[end]` and `self` targets are reserved.)
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
> So a `[[_system]]` block only needs the agent's persona/task — not the JSON contract, the signal list, or the tool input shapes.

- `signal`: Used to resolve next transition.
- `thinking` (optional `string`|`null`): the model's reasoning. Surfaced as a separate `Thinking` trace event (not part of `content`/`FinalOutput`). The auto-appended `RESPONSE FORMAT` block already invites the model to use it.
- `tool_calls`: Filtered to the tools allowed by the current state's `tools` list (matching is **case-insensitive**). Calls naming a disallowed tool — or calls beyond the state's `tool-call-limit` — are **silently dropped** (logged via `Util.Log` only; the model gets **no** error feedback, so it may "think" the tool ran). The surviving allowed calls execute **in parallel** (`Task.WhenAll`), and their results are appended to the conversation.
- `content`: Stored and becomes `FinalOutput` when agent reaches `[end]`.

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
  -> [end] [when: ABORT]
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
