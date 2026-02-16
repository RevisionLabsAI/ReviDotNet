# .rcfg Provider Configuration Files

Provider configuration files (`.rcfg` in the `Providers` directory) define the connection details, protocols, and global settings for AI model providers (e.g., OpenAI, Anthropic, Google, or local vLLM instances).

## File Format Overview

Provider files use an INI-like structure with `[[section]]` headers and `key = value` pairs.

## Sections and Options

### `[[general]]` (Required)
Basic identification and connection info for the provider.

| Option | Type | Description |
| :--- | :--- | :--- |
| `name` | string | Unique identifier for this provider (referenced by model configs). |
| `enabled` | boolean | Whether this provider is available for use. |
| `protocol` | enum | The communication protocol to use. Supported: `OpenAI`, `vLLM`, `Gemini`, `LLamaAPI`, `Claude`. |
| `api-url` | string | The base URL for the API (e.g., `https://api.openai.com/v1/`). |
| `api-key` | string | The API key. Use `environment` to load from an environment variable. |
| `default-model` | string | The fallback model name to use if none is specified. |
| `supports-prompt-completion`| boolean | Whether the provider supports the legacy Completion API (vs Chat API). |

#### Environment Variables for API Keys
If `api-key = environment` is set, ReviDotNet looks for an environment variable named:
`PROVAPIKEY__<PROVIDER_NAME>` (where `<PROVIDER_NAME>` is uppercase and spaces/hyphens are replaced by underscores).

### `[[guidance]]` (Optional)
Settings for constrained output/guidance.

| Option | Type | Description |
| :--- | :--- | :--- |
| `supports-guidance` | boolean | Whether the provider supports structured output guidance (e.g., JSON Schema, GBNF). |
| `default-guidance-type`| enum | The default guidance type if none is specified in the prompt. |
| `_default-guidance-string`| string | (Raw) The default guidance string/schema. |

### `[[limiting]]` (Optional)
Rate limiting and reliability settings.

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `timeout-seconds` | integer | `100` | Request timeout in seconds. |
| `delay-between-requests-ms`| integer | `0` | Delay between consecutive requests to avoid rate limits. |
| `retry-attempt-limit` | integer | `5` | Maximum number of retry attempts for failed requests. |
| `retry-initial-delay-seconds`| integer | `5` | Initial delay before the first retry. |
| `simultaneous-requests` | integer | `10` | Maximum number of concurrent requests to this provider. |

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

[[guidance]]
supports-guidance = false
default-guidance-type = Disabled

[[limiting]]
timeout-seconds = 300
delay-between-requests-ms = 20
retry-attempt-limit = 5
retry-initial-delay-seconds = 5
simultaneous-requests = 5
```
