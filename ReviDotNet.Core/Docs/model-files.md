# Model and Embedding Configuration Files

ReviDotNet uses `.rcfg` files to define specific AI models and their capabilities. These files are categorized into **Inference** (LLMs for text generation) and **Embedding** (models for vector embeddings) and are typically stored in the `Models/Inference` and `Models/Embedding` directories, respectively.

## File Format Overview

Like other ReviDotNet configuration files, model files use an INI-like structure with `[[section]]` headers and `key = value` pairs.

## Name Resolution and Provider Binding

These rules apply to **both** inference and embedding models:

- **Folder prefixing.** A model's effective lookup name is the lower-cased subdirectory path under the load root (`Models/Inference` or `Models/Embedding`), joined with `/`, prepended to the `[[general]] name`. For example a file at `Models/Inference/openai/fast.rcfg` with `name = gpt` resolves to `openai/gpt`. You must pass that **prefixed** name to `modelName` / `Get` / `Find` / `embed.Generate(text, "<name>")` — the bare `name` won't resolve when the file lives in a subfolder (you'll get a null / "Could not find model" result).
- **Provider binding side effects.** A model is bound to its provider by `provider-name`. If `provider-name` is missing, the profile's `Init()` throws and the model is force-disabled. If the named provider is absent or itself disabled, the model is force-disabled (logged) during provider resolution. Either way the model silently **drops out of `Find`/selection** — a perfectly well-formed model can become non-selectable purely because of provider configuration, so check startup logs if a model "disappears".

## Inference Model Sections (`.rcfg`)

Inference models define how ReviDotNet interacts with Large Language Models.

### `[[general]]` (Required)
Basic identification for the model.

| Option | Type | Description |
| :--- | :--- | :--- |
| `name` | string | Unique identifier for this model profile. |
| `enabled` | boolean | Whether this model is available for use. |
| `model-string` | string | The actual model identifier used by the API (e.g., `gpt-4o`, `claude-3-5-sonnet-latest`). |
| `provider-name` | string | The name of the provider this model belongs to (matches the `name` in a provider config). |

### `[[settings]]` (Optional)
Core operational parameters for the model.

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `tier` | enum | `C` | The performance tier of the model. Order is `C` < `B` < `A` (A is highest). Selection (`ModelManager.Find`) returns the **lowest-tier enabled model whose tier ≥ the requested minimum** — so a prompt's `min-tier = B` can match a B or A model, preferring B. Tier parsing is **case-insensitive** (`a`/`A` both work); an empty/unrecognized value is treated as `C` (no minimum). |
| `token-limit` | integer | `0` | The maximum context window size (in tokens). |
| `stop-sequences` | string | `null` | Optional stop sequences to terminate generation. |
| `max-token-type` | enum | `null` | Which OpenAI-style max-tokens field to emit. Allowed values: `MaxTokens` (sends `max_tokens`) or `MaxCompletionTokens` (sends `max_completion_tokens`). When unset, **neither** field is sent. An invalid value throws and the whole model file is skipped — use the exact enum names. |
| `supports-prompt-completion` | boolean | `null` | Whether this specific model supports legacy prompt completion (non-chat) endpoints. Overrides provider-level defaults when set. |
| `supports-response-completion` | boolean | `null` | Whether this specific model supports the Responses API completion endpoint. Overrides provider-level defaults when set. |
| `cost-per-million-input-tokens` | decimal | `null` | USD cost per 1,000,000 prompt/input tokens. Used by `AgentRunner` to enforce `cost-budget` guardrails. When unset, this model contributes 0 to cost tracking (suitable for free or locally-hosted models). |
| `cost-per-million-output-tokens` | decimal | `null` | USD cost per 1,000,000 completion/output tokens. Used by `AgentRunner` to enforce `cost-budget` guardrails. When unset, this model contributes 0 to cost tracking. |

### `[[override-settings]]` (Optional)
Allows this model to override default settings normally found in `.pmt` files.

| Option | Type | Description |
| :--- | :--- | :--- |
| `filter` | string | Override for prompt filter. |
| `chain-of-thought` | string | Override for chain-of-thought behavior. |
| `request-json` | string | Override for JSON request setting. |
| `guidance-schema-type`| enum | Override for output guidance type. |
| `require-valid-output`| boolean| Override for output validation requirement. |
| `retry-attempts` | integer | Override for retry count. |
| `retry-prompt` | string | Override for custom retry instruction. |
| `few-shot-examples` | integer | Override for number of examples to include. |
| `best-of` | string | Override for "best of" count. |
| `max-tokens` | string | Override for maximum tokens to generate. |
| `timeout` | string | Override for request timeout. |
| `preferred-models` | list | Override for preferred models list. |
| `blocked-models` | list | Override for blocked models list. |
| `use-search-grounding`| string | Override for search grounding (e.g., for Gemini). |
| `min-tier` | enum | Override for minimum required tier. |
| `completion-type` | enum | Override for the completion interface type. |

> **Note (inert routing overrides):** `preferred-models`, `blocked-models`, and `min-tier` parse here but are **not consulted during model selection** — model routing (`ModelManager.Find`) reads only the **prompt's** `preferred-models`/`blocked-models`/`min-tier`. The model-profile-level values are surfaced only in the Forge editor UI. Set routing preferences in the `.pmt` `[[settings]]` section instead.

### `[[override-tuning]]` (Optional)
Allows this model to override default sampling parameters. Values are typically strings to allow for a "disabled" state or model-specific defaults.

| Option | Type | Description |
| :--- | :--- | :--- |
| `temperature` | string | Override for randomness. |
| `top-k` | string | Override for top-k sampling. |
| `top-p` | string | Override for nucleus sampling. |
| `min-p` | string | Override for min-p sampling. |
| `presence-penalty` | string | Override for presence penalty. |
| `frequency-penalty` | string | Override for frequency penalty. |
| `repetition-penalty` | string | Override for repetition penalty. |

> **Special values (override mechanism).** In `[[override-settings]]`/`[[override-tuning]]`, the values `default` and `prompt` (any case) are treated as "leave unset" — the property stays null so the **prompt's** value is used. This is how a model profile defers a given setting back to the prompt rather than forcing it. Additionally, the string tuning/override fields honor `disabled` as a literal value meaning "omit this parameter entirely" (the runtime sends nothing for it). So: omit a key to fall through to defaults, set `default`/`prompt` to explicitly defer to the prompt, set `disabled` to suppress the parameter, or set a concrete value to force it.

### `[[input]]` (Optional)
Defines how inputs are formatted for this specific model.

| Option | Type | Description |
| :--- | :--- | :--- |
| `default-system-input-type` | enum | `none` | How system instructions are handled. Options: `none`, `listed`, `filled`, `both`. |
| `default-instruction-input-type`| enum | `listed` | How main instructions are handled. Options: `none`, `listed`, `filled`, `both`. |
| `single-item` | string | *(none)* | Formatting template for a single input item, e.g. `{label}: {text}`. **No built-in default** — required when an input type is `listed`/`both`. |
| `multi-item` | string | *(none)* | Formatting template used when multiple inputs are listed, e.g. `Input #{iterator}: {label}: {text}`. **No built-in default** — required when an input type is `listed`/`both`. |

> **No default templates.** `single-item`/`multi-item` are unset by default (there is no built-in `{label}: {text}` template). If a model's `default-system-input-type` or `default-instruction-input-type` is `listed` or `both` and inputs are supplied, the absence of the matching template causes inference to throw `InvalidOperationException` ("uses a Listed/Both input type but defines no 'single-item'/'multi-item' template"). The `ModelProfileSchemaAnalyzer` (REVI040) also warns about this at build time. Define both templates whenever you use a Listed/Both input type. Placeholders in the templates: `{label}`, `{text}`, and (multi-item) `{iterator}` (1-based).

### Input Type Options

The `default-system-input-type` and `default-instruction-input-type` settings determine how user-provided inputs (via `List<Input>`) are integrated into the prompt:

| Option | Description |
| :--- | :--- |
| `none` | Inputs are not integrated into this section. |
| `listed` | All inputs are formatted (using `single-item` or `multi-item` templates) and appended to the section or inserted into the `{input}` placeholder in the model structure. |
| `filled` | Inputs replace placeholders within the section text. The placeholder is the **identifierized** label: spaces become hyphens and characters outside `[A-Za-z0-9 -]` are stripped (matching is case-insensitive). So an input labeled `Context` fills `{Context}`, but `User Name` fills `{User-Name}` — **not** `{User Name}` or `{UserName}`. |
| `both` | Combines `filled` and `listed`. It first attempts to replace placeholders in the section text with matching inputs. Any inputs that were not used for placeholder replacement are then formatted and appended as a list (or inserted into the `{input}` placeholder). |

### `[[chat-completion]]` (Optional)
Configuration for Chat-based APIs.

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `system-message` | boolean | `true` | Whether to send a separate system role message. |
| `prompt-in-system` | boolean | `false` | Whether to include the main prompt in the system message. |
| `system-in-user` | boolean | `true` | Whether to include system instructions in the first user message. |
| `prompt-in-user` | boolean | `true` | Whether to include the main prompt in the user message. |

> **These defaults are real and apply even if `[[chat-completion]]` is omitted entirely** (they're C# property initializers). This is unlike the enum-typed `[[input]]`/`[[settings]]` fields, which — when the key is absent — fall back to their **first enum member** (e.g. an unset `tier` is `C`, an unset `max-token-type` is null), not a "documented default". So you don't need to restate these chat-completion keys unless you're changing one.

### `[[prompt-completion]]` (Optional)
Detailed template configuration for models using a standard completion (non-chat) interface.

| Option | Type | Description |
| :--- | :--- | :--- |
| `structure` | string | The overall layout (e.g., `{system}{instruction}{input}{example}{output}`). |
| `system-section` | string | Template for the system section. |
| `instruction-section` | string | Template for the instruction section. |
| `input-section` | string | Template for the input section. |
| `example-section` | string | Template for the examples section. |
| `example-structure` | string | Template for individual examples. |
| `example-sub-system` | string | Template for the system part of an example. |
| `example-sub-instruction` | string | Template for the instruction part of an example. |
| `example-sub-input` | string | Template for the input part of an example. |
| `example-sub-output` | string | Template for the output part of an example. |
| `output-section` | string | Template for the final output trigger/prefix. |

## Embedding Model Sections (`.rcfg`)

Embedding models define how ReviDotNet interacts with models used to generate vector representations of text.

### `[[general]]` (Required)
Basic identification for the embedding model.

| Option | Type | Description |
| :--- | :--- | :--- |
| `name` | string | Unique identifier for this embedding profile. |
| `enabled` | boolean | Whether this model is available for use. |
| `model-string` | string | The model identifier used by the API (e.g., `text-embedding-3-small`). |
| `provider-name` | string | The name of the provider hosting this model. |

### `[[settings]]` (Optional)
Core parameters for the embedding model.

| Option | Type | Description |
| :--- | :--- | :--- |
| `tier` | enum | The performance tier (`A`, `B`, or `C`). Used for default selection — see note below. |
| `token-limit` | integer | Maximum tokens per request. **Metadata only — not enforced.** The embedding client does not truncate or validate inputs against this; oversized inputs are sent unchanged. |
| `max-token-type` | enum | Token limit enforcement type. (Not enforced for embeddings.) |

> **Default embedding selection.** When you call `embed.Generate(text)` / `IEmbedService.Generate` without a model name or profile, the registry auto-selects via `Find(minTier: C)` — which returns the **lowest-tier** enabled embedding model (tier order is `C` < `B` < `A`, so `A` is highest). If you want a specific/best model, **pass an explicit model name** rather than relying on the default.

### `[[override-settings]]` (Optional)
Overrides for embedding requests.

| Option | Type | Description |
| :--- | :--- | :--- |
| `max-tokens` | string | Maximum-tokens metadata for this embedding model. **Not enforced** — the embedding client does not truncate inputs by token count; this is informational only. |
| `timeout` | string | Per-model request timeout in seconds (or `disabled`/unset to use the provider default). Honored per request via a linked cancellation; note the provider's `[[limiting]] timeout-seconds` acts as an upper bound. |
| `retry-attempts` | integer | Per-model retry-attempt limit for failed embedding requests, overriding the provider's `[[limiting]] retry-attempt-limit`. |

### `[[embedding-settings]]` (Optional)
Settings specific to the embedding generation.

| Option | Type | Description |
| :--- | :--- | :--- |
| `dimensions` | integer | Number of dimensions for the output vector. |
| `encoding-format` | string | Format for returned embeddings (e.g., `float`, `base64`). |
| `task-type` | string | Task optimization hint (e.g., `retrieval_query`, `classification`). Applied to the request for providers that support it (sent as Gemini's `taskType`); ignored by providers with no task-type concept (e.g. OpenAI). Used as the default when no `taskType` is passed to the embedding API call. |
| `normalize` | boolean | Whether to return unit-length vectors. Used as the default when the embedding API call doesn't pass an explicit `normalize` argument. |

## Usage Examples

### Inference Example (`anth_sonnet_35.rcfg`)

```ini
[[general]]
name = anth_sonnet_35
enabled = true
model-string = claude-3-5-sonnet-latest
provider-name = claude

[[settings]]
tier = A
token-limit = 100000
supports-prompt-completion = true
supports-response-completion = true

[[override-tuning]]
temperature = 1
frequency-penalty = disabled
presence-penalty = disabled
repetition-penalty = disabled

[[input]]
default-system-input-type = none
default-instruction-input-type = listed
single-item = {label}: {text}\n
multi-item = Input #{iterator}: {label}: {text}\n

[[chat-completion]]
system-message = true
prompt-in-system = false
system-in-user = true
prompt-in-user = true
```

### Embedding Example (`oai_text_embedding_3_small.rcfg`)

```ini
[[general]]
name = oai_text_embedding_3_small
enabled = true
model-string = text-embedding-3-small
provider-name = openai

[[settings]]
tier = A
token-limit = 8191

[[embedding-settings]]
dimensions = 1536
encoding-format = float
```
