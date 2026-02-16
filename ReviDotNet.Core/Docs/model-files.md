# Model and Embedding Configuration Files

ReviDotNet uses `.rcfg` files to define specific AI models and their capabilities. These files are categorized into **Inference** (LLMs for text generation) and **Embedding** (models for vector embeddings) and are typically stored in the `Models/Inference` and `Models/Embedding` directories, respectively.

## File Format Overview

Like other ReviDotNet configuration files, model files use an INI-like structure with `[[section]]` headers and `key = value` pairs.

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
| `tier` | enum | `C` | The performance tier of the model (`A`, `B`, or `C`). Used for model selection. |
| `token-limit` | integer | `0` | The maximum context window size (in tokens). |
| `stop-sequences` | string | `null` | Optional stop sequences to terminate generation. |
| `max-token-type` | enum | `null` | How the model handles maximum token limits. |
| `supports-prompt-completion` | boolean | `null` | Whether this specific model supports legacy prompt completion (non-chat) endpoints. Overrides provider-level defaults when set. |
| `supports-response-completion` | boolean | `null` | Whether this specific model supports the Responses API completion endpoint. Overrides provider-level defaults when set. |

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

### `[[input]]` (Optional)
Defines how inputs are formatted for this specific model.

| Option | Type | Description |
| :--- | :--- | :--- |
| `system-input-type` | enum | How system instructions are handled. |
| `instruction-input-type`| enum | How main instructions are handled. |
| `single-item` | string | Formatting template for a single input item. |
| `multi-item` | string | Formatting template for multiple input items. |

### `[[chat-completion]]` (Optional)
Configuration for Chat-based APIs.

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `system-message` | boolean | `true` | Whether to send a separate system role message. |
| `prompt-in-system` | boolean | `false` | Whether to include the main prompt in the system message. |
| `system-in-user` | boolean | `true` | Whether to include system instructions in the first user message. |
| `prompt-in-user` | boolean | `true` | Whether to include the main prompt in the user message. |

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
| `tier` | enum | The performance tier (`A`, `B`, or `C`). |
| `token-limit` | integer | Maximum tokens per request. |
| `max-token-type` | enum | Token limit enforcement type. |

### `[[override-settings]]` (Optional)
Overrides for embedding requests.

| Option | Type | Description |
| :--- | :--- | :--- |
| `max-tokens` | string | Override for maximum tokens to process. |
| `timeout` | string | Override for request timeout. |
| `retry-attempts` | integer | Override for number of retries. |

### `[[embedding-settings]]` (Optional)
Settings specific to the embedding generation.

| Option | Type | Description |
| :--- | :--- | :--- |
| `dimensions` | integer | Number of dimensions for the output vector. |
| `encoding-format` | string | Format for returned embeddings (e.g., `float`, `base64`). |
| `task-type` | string | Task optimization type (e.g., `retrieval_query`, `classification`). |
| `normalize` | boolean | Whether to return unit-length vectors. |

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
system-input-type = None
instruction-input-type = Listed
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
