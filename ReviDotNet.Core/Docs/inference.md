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
- **Automatic Deserialization**: Uses Newtonsoft.Json to map LLM output to your C# class.
- **JSON Extraction**: Automatically finds JSON blocks within Markdown if the model surrounds output with triple backticks.
- **Remediation**: If the JSON is malformed, it automatically attempts to fix it using a `json-fixer` prompt (if available).
- **Validation**: If `require-valid-output = true` is set in the `.pmt` file, it will validate the object structure before returning.

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

It intelligently handles various list formats:
- Bulleted lists (`-`, `*`, `+`)
- Numbered lists (`1.`, `2)`)
- Plain lines

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

It returns `true` for "true", `false` for "false" (case-insensitive), and `null` if the model's output cannot be interpreted as a boolean.

### `ToString`
Returns the raw string output from the model. Useful for creative writing or when you want to handle parsing yourself.

### `Completion`
The base method for all inference. It returns a `CompletionResponse` containing the raw output, metadata (like tokens used), and the selected model profile.

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
1.  The filter prompt is executed with the same inputs as the main request.
2.  The filter prompt is expected to output a specific "canary" value (by default, `"foobar"`) if the input is safe.
3.  If the model outputs *anything else*, ReviDotNet assumes a security threat (or a failed safety check) and throws a `SecurityException`.

## Reliability and Error Recovery

ReviDotNet is designed to handle the stochastic and sometimes unreliable nature of LLMs.

### Retries
The `retry-attempts` setting (in `.pmt` or `.rcfg`) determines how many times a request is retried. Retries can be triggered by:
- API failures (network errors, rate limits).
- Validation failures (when `require-valid-output = true`).
- Parsing failures (malformed JSON in `ToObject`).

### Specialized Fixers
When an inference method fails to parse the model's output, it doesn't just give up. It can use specialized prompts to "clean" the output:
- **`json-fixer`**: Used by `ToObject<T>` to repair broken JSON.
- **`enum-fixer`**: Used by `ToEnum<TEnum>` to map an invalid string to a valid enum member.

These fixer prompts are standard `.pmt` files that you can customize or provide in your configuration.

### JSON Extraction
If a model returns JSON wrapped in Markdown (common with chat-tuned models), ReviDotNet automatically detects the triple backticks (e.g., \` \` \`json ... \` \` \`) and extracts the content inside before parsing.
