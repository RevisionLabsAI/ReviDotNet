# Inference in ReviDotNet

`IInferService` is the primary entry point for performing AI inference in ReviDotNet. It provides a high-level API for interacting with Large Language Models (LLMs), handling prompt construction, model selection, output parsing, and error recovery.

Register it with `services.AddReviDotNet()` and inject it wherever you need inference:

```csharp
public sealed class MyService(IInferService infer)
{
    public Task<string?> SummarizeAsync(string text, CancellationToken token = default)
        => infer.ToString("my-folder/summarize", new Input("Text", text), token: token);
}
```

## Core Concepts

Inference in ReviDotNet revolves around three main components:
1.  **Prompts (`.pmt`)**: Define the task, instructions, and examples.
2.  **Models (`.rcfg`)**: Define the specific LLM to use and its parameters.
3.  **Providers (`.rcfg`)**: Define the API connection details.

`IInferService` orchestrates these components to provide a seamless experience for developers.

## Primary Inference Methods

`IInferService` provides methods to execute prompts and retrieve results in various formats. Most methods have two primary overloads:
1.  **List-based**: Accepts `List<Input>` for multiple inputs.
2.  **Single-input**: Accepts a single `Input` object (or null) for simplicity.

### `ToObject<T>`
Executes a prompt and deserializes the JSON output into a C# object of type `T`. This is the preferred method for structured data.

```csharp
Task<T?> ToObject<T>(
    string promptName,
    List<Input>? inputs = null,
    ModelProfile? modelProfile = null,
    string? modelName = null,
    CancellationToken token = default)
```

**Features:**
- **Requires `request-json = true`**: `ToObject<T>` **throws** if the prompt's `request-json` is not true. (It doesn't constrain the model — see `request-json` in prompt-files.md — but it gates this method.)
- **Automatic Deserialization**: Uses Newtonsoft.Json to map LLM output to your C# class.
- **JSON Extraction**: Automatically finds JSON blocks within Markdown if the model surrounds output with triple backticks (and bounds to the outermost `{}`/`[]`).
- **Remediation**: If the JSON is malformed, it attempts to fix it using a `json-fixer` prompt (one ships embedded). **But** if no JSON can be extracted at all (empty/missing), `ToObject` returns `null` (`default(T)`) **without** invoking the fixer.
- **Validation**: If `require-valid-output = true` is set in the `.pmt` file, it validates the deserialized object (see `require-valid-output` in prompt-files.md) before returning.

### `ToEnum<TEnum>`
Executes a prompt and attempts to parse the result into a specific C# `Enum`.

```csharp
Task<TEnum> ToEnum<TEnum>(
    string promptName,
    List<Input>? inputs = null,
    ModelProfile? modelProfile = null,
    string? modelName = null,
    bool includeEnumValues = false,
    CancellationToken token = default) where TEnum : struct, Enum
```

**Features:**
- **`includeEnumValues`**: If `true`, ReviDotNet automatically injects the valid enum names into the prompt as an input labeled "Enum Values". This helps the model stay within the valid set.
- **Remediation**: If the model provides a value that doesn't match the enum, it attempts to fix it using an `enum-fixer` prompt.

### `ToStringList`
Executes a prompt and parses the output into a `List<string>`. 

```csharp
Task<List<string>> ToStringList(
    string promptName,
    List<Input>? inputs = null,
    ModelProfile? modelProfile = null,
    string? modelName = null,
    CancellationToken token = default)
```

`ToStringList` splits the output on newlines, trims each line, and drops empties — lines are returned **verbatim** (any `- ` / `1. ` markers are preserved).

### `ToStringListClean`
Same as `ToStringList`, but additionally **strips a leading list marker** from each item — a bullet (`-`, `*`, `+`) or an ordinal (`1.`, `2)`) followed by whitespace — so markdown/numbered lists come back as clean values. Use `ToStringList` when you want the raw lines; use `ToStringListClean` when the model returns a formatted list and you want just the contents.

```csharp
Task<List<string>> ToStringListClean(
    string promptName,
    List<Input>? inputs = null,
    ModelProfile? modelProfile = null,
    string? modelName = null,
    CancellationToken token = default)
```

### `ToStringListLimited`
A powerful streaming version of `ToStringList` that allows for early termination based on count or custom logic.

```csharp
Task<List<string>> ToStringListLimited(
    string promptName,
    List<Input>? inputs = null,
    ModelProfile? modelProfile = null,
    string? modelName = null,
    int? maxLines = null,
    Func<string, bool>? evaluator = null,
    CancellationToken token = default)
```

**Parameters:**
- **`maxLines`**: Stops generation immediately after this many non-empty lines are received.
- **`evaluator`**: A predicate that receives the full accumulated string so far. If it returns `true`, the stream is canceled and the results returned.

### `ToBool`
Convenience method for prompts that return a boolean value.

```csharp
Task<bool?> ToBool(
    string promptName,
    List<Input>? inputs = null,
    ModelProfile? modelProfile = null,
    string? modelName = null,
    CancellationToken token = default)
```

It parses the output leniently: it trims whitespace and surrounding quotes/punctuation, compares case-insensitively, and accepts common spellings — `true`/`false`, `yes`/`no`, `y`/`n`, `1`/`0` — so realistic outputs like `true\n` or `"Yes."` work. Returns `null` when the output can't be interpreted as a boolean.

### `ToString`
Returns the raw string output from the model. Useful for creative writing or when you want to handle parsing yourself.

### `Completion`
The base method for all inference. It returns a `CompletionResult` containing the full prompt sent (`FullPrompt`), the candidate outputs (`Outputs`) and the chosen one (`Selected`), the `FinishReason`, and token counts (`InputTokens`, `OutputTokens`). (There is no `CompletionResponse` type and no selected-model-profile field on the result.)

### `CompletionStream`
Provides an `IAsyncEnumerable<string>` for real-time streaming of the model's response.

## Input Handling

Inputs are passed to prompts using the `Input` class. Each input has a `Label` and `Text`.

```csharp
List<Input> inputs = [
    new Input("Context", "User wants a professional tone."),
    new Input("Query", "Write a welcome email.")
];
```

### Single Input Convenience
If your prompt only needs one piece of data, you can use the single-input overload:

```csharp
await infer.ToString("my-prompt", new Input("UserRequest", "Help me!"));
// Or even null if no inputs are required
await infer.ToString("static-prompt", (Input?)null);
```

### Label Matching
In your `.pmt` file, these inputs are typically consumed via the `[[_instruction]]` or `[[_system]]` sections. The way they are formatted (e.g., `Label: Text`) is defined by the `ModelProfile`'s `[[input]]` section.

## Prompt Filtering

ReviDotNet supports automatic input filtering to protect against prompt injection. If a prompt has a `filter` specified in its `[[settings]]` (e.g., `filter = my-safety-prompt`), the inputs are first passed to that filter prompt.

**How it works:**
1.  The filter prompt is executed with the same inputs as the main request (an extra inference call).
2.  The filter prompt is expected to output the **canary** word for safe input. The canary defaults to `safeword` and is configurable per prompt via `[[settings]] filter-canary = <word>`.
3.  If the output doesn't match the canary, ReviDotNet assumes a prompt-injection / failed safety check and throws a `SecurityException`.

**Matching mode** (`[[settings]] filter-matching`):
- `lenient` (default): the comparison trims whitespace, strips surrounding quotes/punctuation, and is case-insensitive — so `Safeword`, `"safeword"`, and `safeword.` all pass.
- `strict`: an exact, case-sensitive, untrimmed match is required.

**Disabling:** omit `filter`, or set `filter = false` (any case), to skip filtering. A filter prompt may not itself declare a `filter`.

## Reliability and Error Recovery

ReviDotNet is designed to handle the stochastic and sometimes unreliable nature of LLMs.

### Retries

There are **two independent** retry mechanisms — set the right one for what you're trying to retry:

- **Transport retries (provider `.rcfg`):** `[[limiting]] retry-attempt-limit` (default `5`) plus `retry-initial-delay-seconds` (default `5`, exponential back-off) govern network/transport failures and non-2xx HTTP responses. There is **no** `retry-attempts` key on a provider — raising it won't add network retries.
- **Output retries (prompt/model):** `retry-attempts` (in a `.pmt` `[[settings]]` or a model `[[override-settings]]`, default `0`) drives only the **application-level** retry loop for output **validation** failures (`require-valid-output = true`) and **parse** failures. It has **no** effect on network/rate-limit retries. The optional `retry-prompt` names a different prompt to use on the retry attempt. Note: a failed provider call is swallowed to a `null`/empty completion (not thrown) for non-streaming calls, and only the converters that actually re-issue act on it — **`ToObject`, `ToStringList`, and `ToEnum` retry**; `ToString`, `ToBool`, and `Completion` just return the `null`/`default` value without re-issuing. So `retry-attempts` is not a general network-resilience knob.

### Inactivity timeout

Each LLM call has an **inactivity (no-data) watchdog** — if the provider sends no data for the configured window, the call aborts with a `TimeoutException` ("...within Ns"). It defaults to **60 seconds** and is **not** a provider `.rcfg` key (it is unrelated to `[[limiting]] timeout-seconds`, which is the overall HTTP timeout). The only override is the prompt/model `timeout` setting (in seconds); when both are set, the **model** value wins over the prompt's.

### Specialized Fixers
When an inference method fails to parse the model's output, it doesn't just give up. It can use specialized prompts to "clean" the output:
- **`json-fixer`**: Used by `ToObject<T>` to repair broken JSON.
- **`enum-fixer`**: Used by `ToEnum<TEnum>` to map an invalid string to a valid enum member.

These fixer prompts are standard `.pmt` files that you can customize or provide in your configuration.

### JSON Extraction
If a model returns JSON wrapped in Markdown (common with chat-tuned models), ReviDotNet automatically detects the triple backticks (e.g., \` \` \`json ... \` \` \`) and extracts the content inside before parsing.

## Forge Gateway Routing

If a `forge.rcfg` is present and enabled, `ForgeManager` activates and `Completion`/`CompletionStream` are routed **remotely through the Forge gateway** instead of calling the provider directly. This is important and **security-relevant**:

- When routing through Forge, the **local pipeline is bypassed** — including `FilterCheck` (the prompt-injection guard described above), local model selection, completion-type parsing, token-limit checks, and the local retry loop. The gateway is expected to own those concerns. **If you rely on `filter` for prompt-injection screening, be aware it does not run locally when Forge routing is active.**
- The `directRoute` parameter (on the `Prompt`-object overloads of `Completion`/`CompletionStream`, default `false`) forces **local** routing for a specific call, bypassing the gateway and running the full local pipeline.

See the Forge gateway docs for the `forge.rcfg` schema and how routing/usage reporting work.
