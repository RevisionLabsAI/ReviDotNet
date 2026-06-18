# .rcfg Provider Configuration Files

Provider configuration files (`.rcfg` in the `Providers` directory) define the connection details, protocols, and global settings for AI model providers (e.g., OpenAI, Anthropic, Google, or local vLLM instances).

## File Format Overview

Provider files use an INI-like structure with `[[section]]` headers and `key = value` pairs.

## Sections and Options

### `[[general]]` (Required)
Basic identification and connection info for the provider.

| Option | Type | Description |
| :--- | :--- | :--- |
| `name` | string | Unique identifier for this provider (referenced by model configs). Like prompts/models, the **effective** name is prefixed with the lower-cased subdirectory path under `RConfigs/Providers/` — a file at `Providers/cloud/openai.rcfg` with `name = openai` resolves to `cloud/openai`. That prefixed name is what model `provider-name` values must reference, and it also flows into the env-var key (the slash is **not** sanitized → `PROVAPIKEY__CLOUD/OPENAI`). Keep provider files directly under `Providers/` unless you specifically want this prefixing. |
| `enabled` | boolean | Whether this provider is available for use. **Note:** `enabled = false` does **not** remove the provider from the registry — it is still loaded and returned by `Get`/`GetAll`, and its HTTP clients are still built. The flag only takes effect when a model/embedding resolves its `provider-name`: a model whose provider is disabled (or missing) is itself force-disabled during resolution. |
| `protocol` | enum | The communication protocol to use. Recognized values: `OpenAI`, `vLLM`, `Gemini`, `Perplexity`, `LLamaAPI`, `Claude`. **Custom dialect** providers — `OpenAI`, `vLLM`, `Gemini`, `Claude` — each have their own request/response shaping. `LLamaAPI` and `Perplexity` have **no dedicated client** and fall through to the **OpenAI** dialect. (The enum comments that mark `LLamaAPI`/`Claude` "Not implemented" are stale — Claude is implemented.) |
| `api-url` | string | The base URL for the API (e.g., `https://api.openai.com/v1/`). |
| `api-key` | string | The API key. Use `environment` to load from an environment variable. |
| `default-model` | string | The fallback model name to use if none is specified. |
| `supports-prompt-completion`| boolean | Whether the provider supports the legacy Completion API (vs Chat API). **Overridden per protocol** — see note below. |
| `supports-response-completion`| boolean | Whether the provider supports the newer Responses API completion endpoint. |

> **Protocol-forced capabilities.** Some capability flags are forced by `protocol` at load and **ignore the file value**: protocol `OpenAI` forces `supports-prompt-completion = false`; protocol `Claude` forces `supports-prompt-completion = true` **and** `supports-guidance = false`. So you can't enable legacy completions on an OpenAI provider, or guidance on a Claude provider, via the file. (A model-level `supports-prompt-completion` can still override the effective per-model value during selection — see model-files.md.)

#### Environment Variables for API Keys
If `api-key = environment` is set, ReviDotNet looks for an environment variable named:
`PROVAPIKEY__<PROVIDER_NAME>` (where `<PROVIDER_NAME>` is uppercase and spaces/hyphens are replaced by underscores).

### `[[guidance]]` (Optional)
Settings for constrained output/guidance.

| Option | Type | Description |
| :--- | :--- | :--- |
| `supports-guidance` | boolean | Whether the provider supports structured output guidance (e.g., JSON Schema, GBNF). |
| `default-guidance-type`| enum | Default schema strategy used when a prompt defers via `[[settings]] guidance-schema-type = defer`. One of `disabled`, `json-auto`, `json-manual`, `regex-auto`, `regex-manual`, `gnbf-auto`, `gnbf-manual`. *Auto* generates a schema from the requested output type; *manual* uses the `[[_default-guidance-string]]` raw section below. |

#### `[[_default-guidance-string]]` (Optional raw section)

The default guidance schema/string used by *manual* `default-guidance-type` strategies. Because its key begins with `_`, it is a **raw section** — write it as its own `[[_default-guidance-string]]` block with the schema as the body, **not** as a `_default-guidance-string = …` line under `[[guidance]]` (that key-value form is silently ignored). It is consumed only on the provider-default deferral path (`guidance-schema-type = defer`).

```ini
[[guidance]]
supports-guidance = true
default-guidance-type = json-manual

[[_default-guidance-string]]
{
  "type": "object",
  "properties": { "answer": { "type": "string" } },
  "required": ["answer"]
}
```

### `[[limiting]]` (Optional)
Rate limiting and reliability settings.

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `timeout-seconds` | integer | `100` | Request timeout in seconds. |
| `delay-between-requests-ms`| integer | `0` | Delay between consecutive requests to avoid rate limits. |
| `retry-attempt-limit` | integer | `5` | Maximum number of retry attempts for failed requests. |
| `retry-initial-delay-seconds`| integer | `5` | Initial delay before the first retry. |
| `simultaneous-requests` | integer | `10` | Maximum number of concurrent requests to this provider. |

> **Not the whole timeout story.** `timeout-seconds` is the overall HTTP request timeout. A **separate inactivity (response-headers) watchdog** — `InactivityTimeoutSeconds`, default **60s** — aborts hung connections that send no data; it has **no `.rcfg` key** and is overridable only per-request via the prompt/model `timeout` setting (see inference.md). Also note that an **absent `default-model`** resolves differently per client: the inference client falls back to `"default"`, while the embedding client falls back to `"text-embedding-ada-002"`.

> **Special values (`default` / `prompt`).** As with prompt/model configs, a provider value whose lowercase form is `default` or `prompt` is a reserved **skip sentinel** — the key is left unset and the per-protocol/client fallback applies. So `default-model = default` does **not** select a model literally named "default"; it leaves `default-model` unset (and then the per-client fallback above applies).

## Usage Example (`claude.rcfg`)

```ini
[[general]]
name = claude
enabled = true
protocol = Claude
api-url = https://api.anthropic.com/
api-key = environment
default-model = claude-3-5-sonnet-latest
supports-prompt-completion = true
supports-response-completion = true

[[guidance]]
# For Protocol.Claude these are forced at load: supports-guidance is always false and
# supports-prompt-completion is always true — the file values here are ignored (see the note above).
supports-guidance = false
default-guidance-type = Disabled

[[limiting]]
timeout-seconds = 300
delay-between-requests-ms = 20
retry-attempt-limit = 5
retry-initial-delay-seconds = 5
simultaneous-requests = 5
```
