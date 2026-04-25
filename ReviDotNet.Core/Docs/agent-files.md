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
| `version` | integer | Optional version number. |
| `description` | string | Optional description for maintainers. |

### `[[loop]]` (Required)

| Option | Type | Description |
| :--- | :--- | :--- |
| `entry` | string | Entry state name. Must exist in `[[state.<name>]]`. |

### `[[state.<name>]]` (At least one required)

Defines each state.

| Option | Type | Description |
| :--- | :--- | :--- |
| `description` | string | Optional human-readable state summary. |
| `prompt` | string | Reserved for prompt reference metadata; currently parsed but not applied by `AgentRunner`. |
| `model` | string | Optional model profile name override for this state. |
| `tools` | list | Comma/space-separated tool names allowed in this state. |

### `[[state.<name>.guardrails]]` (Optional)

All values are optional.

| Option | Type | Description |
| :--- | :--- | :--- |
| `cycle-limit` | integer | Max activations of this state across a run. |
| `max-steps` | integer | Max LLM calls per activation. |
| `timeout` | integer | Max seconds per activation. |
| `cost-budget` | decimal | Parsed but not currently enforced by `AgentRunner`. |
| `tool-call-limit` | integer | Max tool calls per activation. |
| `retry-limit` | integer | Parsed but not currently enforced by `AgentRunner`. |
| `loop-detection` | boolean | Enables repeated-traversal loop detection. |

### `[[_system]]` (Optional)

Global system text applied to every step.

### `[[_state.<name>.instruction]]` (Optional)

State-specific instruction appended to the global system text.

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
- Transition targets currently match word characters (`[A-Za-z0-9_]`), so avoid hyphens in state names.

## LLM Step Contract

Each step response is expected as JSON:

```json
{
  "signal": "READY",
  "tool_calls": [
    { "name": "web-search", "input": "best .NET tracing libs 2026" }
  ],
  "content": "Found 5 options. Ready to summarize."
}
```

- `signal`: Used to resolve next transition.
- `tool_calls`: Filtered to tools allowed by current state's `tools`.
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
2. DI facade: `IAgentManager` / `AgentRegistry`

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
