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
| `filter` | string | `null` | Optional filter criteria for the prompt.                                                                                                                                                                                                                                                               |
| `chain-of-thought` | boolean | `false` | If `true`, encourages the model to reason step-by-step.                                                                                                                                                                                                                                                |
| `request-json` | boolean | `false` | If `true`, requests the model to output in JSON format.                                                                                                                                                                                                                                                |
| `guidance-schema-type` | enum | `disabled` | Specifies the schema type for guided output. Options are described in more detail in a section below, but here are the options at a glance: `disabled`, `default`, `regex-manual`, `regex-auto`, `json-manual`, `json-auto`, `gnbf-manual`, `gnbf-auto`.                                               |
| `require-valid-output` | boolean | `false` | If `true`, validates output against the provided schema.                                                                                                                                                                                                                                               |
| `retry-attempts` | integer | `0` | Number of times to retry on failure.                                                                                                                                                                                                                                                                   |
| `retry-prompt` | string | `default` | Custom instruction used during retries.                                                                                                                                                                                                                                                                |
| `few-shot-examples` | integer | `all` | Number of examples to include in the prompt context.                                                                                                                                                                                                                                                   |
| `best-of` | integer/string | `1` | Request multiple completions and return the best one.                                                                                                                                                                                                                                                  |
| `max-tokens` | integer | `model default` | Maximum number of tokens to generate.                                                                                                                                                                                                                                                                  |
| `timeout` | integer/string | `30` | Request timeout in seconds.                                                                                                                                                                                                                                                                            |
| `use-search-grounding`| boolean | `false` | If `true`, enables search-based grounding (if supported by model).                                                                                                                                                                                                                                     |
| `preferred-models` | list | `null` | Comma/space-separated list of preferred models (e.g., `gpt-4o, groq-llama-3`).                                                                                                                                                                                                                         |
| `blocked-models` | list | `null` | List of models that should never be used.                                                                                                                                                                                                                                                              |
| `min-tier` | string | `C` | Minimum model tier required. Options: `A` (Highest), `B` (Mid), `C` (Lowest).                                                                                                                                                                                                                          |
| `completion-type` | string | `auto` | The type of completion interface to use. Options: `chat-only`, `prompt-only`, `prompt-chat-one`, `prompt-chat-multi`.                                                                                                                                                                                  |
| `system-input-type-override` | enum | `null` | Overrides the model's `system-input-type`. Options: `None`, `Listed`, `Filled`.                                                                                                                                                                                                                       |
| `instruction-input-type-override` | enum | `null` | Overrides the model's `instruction-input-type`. Options: `None`, `Listed`, `Filled`.                                                                                                                                                                                                                  |

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

### Raw Content Sections
These sections capture the core text of the prompt.

*   `[[_system]]` (**Required**): Defines the system instructions/persona for the AI.
*   `[[_instruction]]` (**Required**): The main task or instruction for the AI.
*   `[[_schema]]` (Optional): A JSON, Regex, or GBNF schema defining the expected output structure. Used when `guidance-schema-type` is set to a `Manual` variant.

### Examples in .pmt Files

Examples are used for "few-shot" prompting. They are defined in numbered pairs:

*   `[[_exin_N]]`: The input for the N-th example.
*   `[[_exout_N]]`: The expected output for the N-th example.

ReviDotNet automatically pairs these by their index (`N`).

#### Input Formatting (`[[_exin_N]]`)

The input section often uses a labeled format to organize multiple pieces of information for the LLM. 

**Note on Brackets:**
- **Square Brackets `[]`**: Used for labels in listed input (e.g., `[Context]`).
- **Curly Brackets `{}`**: Used for placeholders that are inserted into the prompt (e.g., `{Total Names}`).

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
| `json-manual` | Uses the JSON Schema provided in the `[[_schema]]` section. |
| `json-auto` (Preferred) | Automatically generates a JSON Schema based on the C# return type of the function calling the prompt. Best paired with `ToObject<T>()` to deserialize the validated JSON directly into your C# type. |
| `regex-manual` | Uses the regular expression provided in the `[[_schema]]` section to guide output. |
| `regex-auto` | Automatically generates a regex based on the C# return type. If `chain-of-thought` is enabled, it includes a "Reasoning: ... Output: ..." wrapper. |
| `gnbf-manual` | Uses a GBNF (Grammar-Based Next-token Filtering) grammar provided in `[[_schema]]`. |
| `gnbf-auto` | Automatically generates a GBNF grammar based on the C# return type. |

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
