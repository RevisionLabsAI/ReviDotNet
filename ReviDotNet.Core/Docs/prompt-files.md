# .pmt Prompt Configuration Files

`.pmt` files are used by ReviDotNet to define AI prompts, their settings, tuning parameters, and examples in a structured, human-readable format.

## File Format Overview

The `.pmt` format is an INI-like structure using `[[section]]` headers. It supports two types of sections:

1.  **Key-Value Sections**: Contain standard `key = value` pairs.
2.  **Raw Content Sections**: Identified by a leading underscore (e.g., `[[_system]]`). These treat all lines following the header as a single multi-line string.

### YAML vs JSON (Preference)

When embedding structured content inside `.pmt` files (for example in `[[_exout_N]]` example outputs or other raw content blocks), prefer YAML over JSON.

- YAML is more token-efficient for LLMs and easier for humans to read and edit inline.
- Keep example payloads in YAML even when you ultimately want JSON from the model; set `[[settings]] -> request-json = true` and ReviDotNet will request/validate JSON while your examples can remain YAML.
- Use JSON only when an exact JSON byte structure is required or when demonstrating a precise JSON schema/output.

## Parsing Rules

The INI-like parser has a few behaviors worth knowing:

- **Comments**: `#` starts a comment only when it is the **first non-whitespace character** of a line, and only in key-value sections (`[[information]]`, `[[settings]]`, `[[tuning]]`). An inline `#` (e.g. `name = a # b`) is preserved as part of the value. Inside raw sections (`[[_system]]`, `[[_instruction]]`, `[[_exout_N]]`, …) `#` is never a comment.
- **Blank lines**: inside raw sections (`[[_system]]`, `[[_instruction]]`, `[[_exin_N]]`, `[[_exout_N]]`, …) blank lines are **preserved**, so you can separate paragraphs in system text and format multi-line/multi-paragraph examples naturally. Leading and trailing blank lines of a raw section are trimmed, but internal ones are kept verbatim. In key-value sections (`[[information]]`/`[[settings]]`/`[[tuning]]`) blank lines are ignored.
- **Section headers** must be a line of the exact form `[[name]]` with nothing after the closing `]]`. Trailing text on a header line breaks header recognition.
- **Literal `[[…]]` inside a raw section**: prefix the line with a backslash — `\[[not a header]]` — and the parser emits the literal `[[not a header]]` without ending the raw block (the backslash is stripped).

## Effective Name and Versioning

A prompt's **effective name** — the string you pass to `ToObject`/`ToString`/etc. — is:

> `<lower-cased subfolder path under RConfigs/Prompts/>` + `/` (when non-empty) + the `[[information]] name` value.

The physical filename is **ignored**. For example, a file at `RConfigs/Prompts/Search/anything.pmt` with `name = analyze-specs` resolves to `search/analyze-specs`. Lookups (`Get`) match this effective name **exactly and case-sensitively**, so `Get("analyze-specs")` for a prompt in a subfolder returns null — use `"search/analyze-specs"`.

**Versioning / duplicates:** when two loaded prompts resolve to the same effective name, a later one replaces an earlier one **only if its `version` is strictly greater**; a reload at an equal or lower version does not win. (This is how your own same-named prompt overrides a built-in embedded default.)

## Sections and Options

### `[[information]]` (Required)
Metadata about the prompt itself. All items in this section are required.

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `name` | string | `null` | Unique identifier for the prompt. |
| `version` | integer | `1` | Incremental version number. |

### `[[settings]]` (Optional)
Operational settings that govern how the prompt is executed. All items in this section are optional.

| Option | Type | Default | Description                                                                                                                                                                                                                                                                                            |
| :--- | :--- | :--- |:-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `filter` | string | `null` | The **name of another prompt** used as a prompt-injection screen. When set, that filter prompt is run over the same inputs *before* the main request; it must output exactly `foobar` for the input to be considered safe — any other output throws a `SecurityException` (and adds an extra inference call). Set to `false` (any case) or omit to disable. A filter prompt may not itself declare a `filter`. See "Prompt Filtering" in inference.md. |
| `filter-canary` | string | `safeword` | The exact word the `filter` prompt must emit for input to be considered safe. See "Prompt Filtering" in inference.md. |
| `filter-matching` | string | `lenient` | How the filter output is compared to the canary: `lenient` (trim, strip surrounding quotes/punctuation, case-insensitive) or `strict` (exact, case-sensitive). |
| `chain-of-thought` | boolean | `false` | If `true`, encourages the model to reason step-by-step.                                                                                                                                                                                                                                                |
| `request-json` | boolean | `false` | **Does NOT constrain the model.** It (a) enables `ToObject<T>()` — which throws if `request-json` is false — and (b) converts YAML example outputs to JSON in the prompt context. It adds no payload key and does not ask the provider for JSON. For actual on-wire JSON enforcement use `guidance-schema-type`. Note `ToObject` returns `null` (no fixer) if the extracted JSON is empty.                                                                                                                                                                              |
| `guidance-schema-type` | enum | `disabled` | Specifies the schema type for guided output. Options are described in more detail in a section below, but here are the options at a glance: `disabled`, `defer`, `regex-manual`, `regex-auto`, `json-manual`, `json-auto`, `gnbf-manual`, `gnbf-auto`. (Note: the bare value `default` is treated as "unset/skip" — it applies **no** guidance, and is now **flagged with a warning** at load time and by analyzer `REVI006`. To inherit the provider's default strategy use `defer`; to be explicit about no guidance use `disabled`.) A *-manual strategy reads the `[[_schema]]` section; analyzer `REVI010` cross-checks that the schema is present, well-formed JSON (for `json-manual`), and not orphaned under an *-auto/disabled strategy. See also the **guidance capability matrix** in provider-files.md — a strategy the target provider can't enforce is warned at runtime. |
| `require-valid-output` | boolean | `false` | If `true`, validates the **deserialized object** (in `ToObject<T>`) via reflection — it checks `[Required]` members (non-null / non-empty) and Min/Max Items/Length attributes on collections. This is **not** JSON-Schema validation and does not check the `[[_schema]]`. A validation failure triggers the app-level retry (see `retry-attempts`).                                                                                                                                                                                |
| `retry-attempts` | integer | `0` | Number of times to retry on failure.                                                                                                                                                                                                                                                                   |
| `retry-prompt` | string | `default` | Custom instruction used during retries.                                                                                                                                                                                                                                                                |
| `few-shot-examples` | integer | `all` | Number of examples to include in the prompt context.                                                                                                                                                                                                                                                   |
| `best-of` | integer/string | `1` | Request multiple completions and return the best one.                                                                                                                                                                                                                                                  |
| `max-tokens` | integer | `model default` | Maximum number of tokens to generate.                                                                                                                                                                                                                                                                  |
| `timeout` | integer/string | `30` | Request timeout in seconds.                                                                                                                                                                                                                                                                            |
| `use-search-grounding`| boolean | `false` | If `true`, enables search-based grounding (if supported by model).                                                                                                                                                                                                                                     |
| `thinking` | string | `null` | Per-request native thinking/reasoning amount as one of the five common words `minimal`/`low`/`medium`/`high`/`max`, or `none` to disable. **Overrides** the model's default `thinking` for this prompt; when **unset (the default), the prompt inherits the model's `thinking`**. The model's `thinking-conversion-*` table still translates the word into the provider value (Claude effort / Gemini budget / OpenAI `reasoning_effort`). See "Native thinking / reasoning" in `model-files.md`. |
| `preferred-models` | list | `null` | Comma/space-separated list of preferred models (e.g., `gpt-4o, groq-llama-3`).                                                                                                                                                                                                                         |
| `blocked-models` | list | `null` | List of models that should never be used.                                                                                                                                                                                                                                                              |
| `min-tier` | string | `C` | Minimum model tier required: `A` (Highest), `B` (Mid), `C` (Lowest). On the prompt this is stored as a **raw string** (no enum validation or default at parse time) and interpreted by `ModelManager.Find` — **case-insensitively** (`a`/`A` both work); an unrecognized/typo value means "no minimum", i.e. effectively `C`. Selection returns the lowest-tier enabled model whose tier ≥ this minimum. |
| `completion-type` | string | `auto` | The completion interface to use. Options: `chat-only`, `prompt-only`, `prompt-chat-one`, `prompt-chat-multi`, or `auto` (unset → `chat-only`). These kebab values are normalized at runtime. A model profile's `[[override-settings]] completion-type` (a strict `CompletionType` enum) overrides this when set. The `Prompt.IsCompletion()` helper (used for model selection) recognizes all of these forms — the three `prompt-*` types prefer a completion-capable provider (falling back to any model when none exists), while `chat-only`/`auto` do not. |
| `system-input-type-override` | enum | `null` | Overrides the model's `system-input-type`. Options: `None`, `Listed`, `Filled`, `Both`. (`Both` = fill matching `{placeholders}` first, then list any inputs that weren't used for a placeholder.) |
| `instruction-input-type-override` | enum | `null` | Overrides the model's `instruction-input-type`. Options: `None`, `Listed`, `Filled`, `Both`. (`Both` = fill matching `{placeholders}` first, then list the remainder.) |
| `strict-inputs` | boolean | `false` | Validates input usage when the prompt is rendered. When the rendered prompt still contains an **unfilled `{placeholder}`** (no input matched it), or a provided input matched no placeholder and was **dropped** (pure `Filled` mode), ReviDotNet logs a warning. With `strict-inputs = true` it **throws** instead, so a mistyped label/placeholder fails fast (e.g. in CI) rather than shipping literal braces. Default (false/unset) is warn-only and never changes existing behavior. |

### `[[tuning]]` (Optional)
Parameters to control the model's sampling behavior. All items in this section are optional.

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `temperature` | float | `1.0` | Controls randomness (0.0 to 2.0). Higher is more creative. |
| `top-k` | integer | `model default` | Limits sampling to the top K most likely tokens. |
| `top-p` | float | `1.0` | Nucleus sampling; considers tokens with cumulative probability P. |
| `min-p` | float | `0.0` | Minimum probability threshold for a token to be considered. |
| `presence-penalty` | float | `0.0` | Penalizes tokens based on their presence in the text so far. |
| `frequency-penalty` | float | `0.0` | Penalizes tokens based on their frequency in the text so far. |
| `repetition-penalty` | float | `1.0` | Penalizes tokens that have already appeared. |

> **Special values (`default` / `prompt`).** In any `[[settings]]`/`[[tuning]]` key, a value whose lowercase form is `default` (or `prompt`) is a reserved **skip sentinel** — the property is left **unset** (null), not assigned that literal string. This is how you clear/omit a setting. Notably, `retry-prompt = default` does **not** set a retry prompt named "default" — it disables the retry-prompt override (leaves it null). To use a literal value, don't use these reserved words.

### Raw Content Sections
These sections capture the core text of the prompt.

> **At least one** of `[[_system]]` or `[[_instruction]]` is required (not both). Loading fails only if *both* are empty/absent; either one alone is valid.

*   `[[_system]]`: Defines the system instructions/persona for the AI.
*   `[[_instruction]]`: The main task or instruction for the AI.
*   `[[_schema]]` (Optional): A JSON, Regex, or GBNF schema defining the expected output structure. Used when `guidance-schema-type` is set to a `Manual` variant.

### Examples in .pmt Files

Examples are used for "few-shot" prompting. They are defined in numbered pairs:

*   `[[_exin_N]]`: The input for the N-th example.
*   `[[_exout_N]]`: The expected output for the N-th example.

ReviDotNet automatically pairs these by their index (`N`). **Both halves are required** — an `[[_exin_N]]` with no matching `[[_exout_N]]` (or vice versa) is a half-pair that gets **silently dropped** when the prompt loads. To catch the off-by-one/typo that causes this, ReviDotNet:
- logs a load-time warning (`example N … is missing its input/output side`), and
- raises analyzer warning **REVI009** at build time when your `.pmt` files are included as `AdditionalFiles` (see `analyzers.md`).

#### Input Formatting (`[[_exin_N]]`)

The input section often uses a labeled format to organize multiple pieces of information for the LLM. 

**Note on Brackets:**
- **Square Brackets `[]`**: Used for labels in listed input (e.g., `[Context]`).
- **Curly Brackets `{}`**: Used for placeholders that are inserted into the prompt. The placeholder name is the **identifierized** form of the input label: spaces become hyphens and characters outside `[A-Za-z0-9 -]` are stripped (matching is case-insensitive). So an input labeled `[Total Names]` is filled via the placeholder `{Total-Names}` — **not** `{Total Names}`. Single-word labels like `[Context]` are unchanged (`{Context}`).

**Example:**
```ini
[[_exin_1]]
[Context]
The user is looking for a professional name for a law firm.
[Keywords]
Justice, Integrity, Shield
[Request]
Generate 5 names.
```

The `[Label]` format is parsed by the `Prompt.ExtractInputs` utility, which splits the content into distinct labeled segments. These labels are then used based on the `input-type` settings:

- **Listed**: Each labeled segment is formatted according to the model's `single-item` or `multi-item` templates and presented as a list.
- **Filled**: The text in `[[_system]]` or `[[_instruction]]` can contain placeholders like `{Context}` or `{Keywords}` which will be replaced by the corresponding text from the labeled input.

**Filled Input Example:**
```ini
[[settings]]
instruction-input-type-override = Filled

[[_instruction]]
Analyze the following context: {Context}
Then, using these keywords: {Keywords}
{Request}
```

#### Output Formatting (`[[_exout_N]]`)

The output section typically contains either **Plain Text**, **YAML**, or **JSON**. 

*   **Plain Text**: Used for simple completion tasks.
*   **YAML (Preferred)**: Strongly preferred for structured data in examples. YAML is more token‑efficient than JSON and easier for humans to write and maintain within a `.pmt` file. ReviDotNet will still request/validate JSON at runtime when `request-json = true`, so examples can remain YAML.
*   **JSON (Use only when necessary)**: Reserve for cases where an exact JSON shape/byte structure must be demonstrated or enforced in the example itself.

**YAML Example:**
```ini
[[settings]]
request-json = true

[[_exout_1]]
Status: success
Count: 3
Names:
  - AlphaLaw
  - JusticeShield
  - IntegrityLegal
```

**JSON Example:**
```ini
[[settings]]
request-json = true

[[_exout_1]]
{
  "status": "success",
  "count": 3,
  "names": ["AlphaLaw", "JusticeShield", "IntegrityLegal"]
}
```

### Output Structure and Guidance

ReviDotNet supports multiple ways to enforce the structure of the AI's response using the `guidance-schema-type` setting and the `[[_schema]]` section.

#### Guidance Schema Types

The `guidance-schema-type` in `[[settings]]` determines how the output is constrained:

| Type | Description |
| :--- | :--- |
| `disabled` | No output guidance is enforced. |
| `defer` | Defers to the provider's configured default guidance strategy (`[[guidance]] default-guidance-type` / `_default-guidance-string` in the provider `.rcfg`). Use this to inherit a provider-wide default instead of choosing a strategy per prompt. The bare value `default` does **not** do this — it is parsed as "unset/skip" and applies no guidance. |
| `json-manual` | Uses the JSON Schema provided in the `[[_schema]]` section. |
| `json-auto` (Preferred) | Automatically generates a JSON Schema from the C# return type. See the note below on the **generated-schema shape** (kebab-case names, nullability disabled, strict-mode constraints). Best paired with `ToObject<T>()`. |
| `regex-manual` | Uses the regular expression provided in the `[[_schema]]` section to guide output. |
| `regex-auto` | Automatically generates a regex based on the C# return type. If `chain-of-thought` is enabled, it includes a "Reasoning: ... Output: ..." wrapper. |
| `gnbf-manual` | **Not yet implemented (no-op).** GBNF/grammar guidance has no producer wired in — selecting this currently applies **no** guidance. |
| `gnbf-auto` | **Not yet implemented (no-op).** No GBNF grammar is generated — selecting this currently applies **no** guidance. |

> **`json-auto` generated-schema shape.** The auto-generated JSON Schema (`Util.JsonStringFromType`) forces **kebab-case** property names and **disables nullability**. Under OpenAI strict mode it additionally forces an object root, `additionalProperties: false`, and marks **all** properties `required` (recursively). When deserializing with `ToObject<T>()`, account for this: expect kebab-case keys (use matching JSON property attributes / naming) and don't rely on optional fields.

> **Value parsing & aliases.** `guidance-schema-type` values are case-insensitive and ignore `-`/`_` (so `json-auto`, `json_auto`, and `JsonAuto` are equivalent). The parser also accepts bare aliases that map to the **manual** variants: `json` → `json-manual`, `regex` → `regex-manual`, and `gbnf` → `gnbf-manual`. Note the spelling: the bare alias is `gbnf` (transposed) while the full kebab forms are `gnbf-manual`/`gnbf-auto` (matching the `GNBF…` enum members).

##### Recommendation: Prefer `json-auto` with `ToObject<T>()`

When you have a well-defined C# return type, prefer `json-auto` over manual schemas. With `json-auto`, ReviDotNet derives the JSON Schema from your C# type, requests JSON from the model, validates it, and you can then call `ToObject<T>()` to deserialize the result into your target type.

Benefits of this approach:

- Single source of truth: your C# type defines the structure — no duplicated JSON schema to maintain.
- Stronger guarantees: automatic validation prior to deserialization reduces runtime errors.
- Simpler prompts: fewer tokens and less boilerplate compared to embedding manual schemas.

Minimal example:

```ini
[[settings]]
request-json = true
guidance-schema-type = json-auto
```

```csharp
// Given a method that runs the prompt and returns a result wrapper
// with JSON already validated against the auto-generated schema:
MyType value = result.ToObject<MyType>();
```

#### Using `[[_schema]]`

The content of the `[[_schema]]` section is interpreted based on the selected `guidance-schema-type`.

Note: For most use cases with a known C# return type, you do not need `[[_schema]]`. Prefer `json-auto` and deserialize with `ToObject<T>()`. Use `[[_schema]]` only when you must hand-author a specific schema/grammar or when the shape cannot be expressed conveniently as a C# type.

**JSON Schema (`json-manual`)**
Provide a standard JSON Schema (Draft 4, 7, etc. depending on the model provider).

```ini
[[settings]]
guidance-schema-type = json-manual

[[_schema]]
{
  "type": "object",
  "properties": {
    "name": { "type": "string" },
    "age": { "type": "integer" }
  },
  "required": ["name", "age"]
}
```

**Regex Guidance (`regex-manual`)**
Provide a regular expression that the model must follow. Note that complex regexes may only be supported by certain providers (like Llama.cpp/Groq/vLLM via GBNF translation).

```ini
[[settings]]
guidance-schema-type = regex-manual

[[_schema]]
[A-Z][a-z]+, [A-Z][a-z]+
```

**GBNF Grammar (`gnbf-manual`)**
Provide a GBNF grammar for fine-grained control over the output structure.

```ini
[[settings]]
guidance-schema-type = gnbf-manual

[[_schema]]
root   ::= object
object ::= "{" ws ( pair ( "," ws pair )* )? "}"
pair   ::= string ":" ws value
...
```

#### Chain of Thought and Schemas

When `chain-of-thought = true` is used with `regex-auto`, the generated guidance typically expects the model to output its reasoning before the structured data:

```text
Reasoning: <thought process>
Output: <structured data>
```

ReviDotNet's `RegexGenerator` automatically handles this wrapping when `RegexAuto` is selected.

## Usage Example

```ini
[[information]]
name = sample-prompt
version = 1

[[settings]]
chain-of-thought = true
max-tokens = 500
preferred-models = gpt-4o

[[tuning]]
temperature = 0.7

[[_system]]
You are a helpful coding assistant.

[[_instruction]]
Write a C# function based on the following requirements.

[[_exin_1]]
Create a function that adds two numbers.

[[_exout_1]]
public int Add(int a, int b) => a + b;
```
