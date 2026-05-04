# ReviDotNet

ReviDotNet is a .NET library that makes working with modern LLMs straightforward, safe, and easily configurable. It separates prompt logic, provider connections, and model settings into simple repository files, adds resilience (retries, validators, output fixers), and provides Roslyn analyzers to catch mistakes at build time.

> Note: ReviDotNet is still in development and some features may not be fully implemented yet. 

## Features

- Configuration-as-files (kept in your repo)
  - Prompts: `.pmt` files with sections for `[[information]]`, `[[settings]]`, `[[tuning]]`, raw prompt content, and examples
  - Providers: `.rcfg` files describing API host, keys, and protocol per provider
  - Models: `.rcfg` files describing model profiles (limits, defaults, overrides)
- Supports multiple providers and models with configurable model routing
- Chat and prompt completion interfaces (automatically chosen or forced per prompt/model)
- Agent orchestration via `.agent` files (state loops, transitions, tool gating, guardrails)
- Structured output guidance options (JSON/Regex/GBNF – manual and auto variants)
- Resilience: retries, timeout control, token accounting, safe JSON extraction from Markdown
- Built‑in fixers for common issues (e.g., `json-fixer` and `enum-fixer` prompts)
- Input filtering and optional safety canary to detect injection attempts
- Simple, strongly-typed inference API: `ToObject<T>`, `ToEnum<TEnum>`, `ToStringList`, `ToStringListLimited`, `ToBool`, `ToString`, streaming via `CompletionStream`
- First-class Roslyn analyzers for prompt and agent validation (`REVI001`, `REVI006`, `REVI007`, `REVI008`)
- Embeddings support via model profiles dedicated to embeddings

## Why ReviDotNet?

ReviDotNet was built for ease of use with a combination of unique features that prioritize repository-centric configuration, compile-time safety, and built-in resilience. While other libraries provide broad abstractions, ReviDotNet focuses on a structured, file-based approach that keeps your prompts and model settings versioned alongside your code.

| Feature | ReviDotNet | Semantic Kernel | MEAI | LangChain.NET |
|---|---|---|---|---|
| File-based prompt config (`.pmt`/`.yaml`) | ✅ Rich | ✅ Basic | ❌ | ❌ |
| File-based provider/model config | ✅ `.rcfg` | ⚠️ `appsettings` | ⚠️ Code | ⚠️ Code |
| Built-in agent orchestration | ✅ Full | ✅ Full | ⚠️ Partial | ⚠️ Partial |
| Built-in model routing | ✅ | ❌ | ❌ | ❌ |
| Roslyn compile-time analyzer | ✅ | ❌ | ❌ | ❌ |
| Strongly-typed inference API | ✅ | ✅ | ⚠️ | ⚠️ |
| Built-in JSON/enum fixers | ✅ | ⚠️ | ❌ | ❌ |
| Injection canary | ✅ | ❌ | ❌ | ❌ |
| Embeddings | ✅ | ✅ | ✅ | ✅ |
| Streaming | ✅ | ✅ | ✅ | ✅ |
| Multi-provider | ✅ | ✅ | ✅ | ✅ |

## Repository layout (ReviDotNet)

- `ReviDotNet.Core` – main runtime (config parsing, providers, models, inference API)
- `ReviDotNet.Analyzers` – Roslyn analyzers (prompt/agent validation at compile time)
- `ReviDotNet.Tests` – unit tests and helpers

Your app’s configuration files typically live under an `RConfigs` folder in your project:

- `RConfigs/Prompts` – `.pmt` prompt files (any subfolders)
- `RConfigs/Agents` – `.agent` orchestration files (any subfolders)
- `RConfigs/Providers` – provider `.rcfg` files
- `RConfigs/Models/Inference` – inference model `.rcfg` files
- `RConfigs/Models/Embedding` – embedding model `.rcfg` files

## Documentation

The core docs are in this repo:

- Prompt files: `ReviDotNet.Core/Docs/prompt-files.md`
- Agent files: `ReviDotNet.Core/Docs/agent-files.md`
- Provider files: `ReviDotNet.Core/Docs/provider-files.md`
- Model files: `ReviDotNet.Core/Docs/model-files.md`
- Inference API: `ReviDotNet.Core/Docs/inference.md`
- Analyzers: `ReviDotNet.Core/Docs/analyzers.md`

## Quick start

1) Add ReviDotNet to your solution

- Reference the `ReviDotNet.Core` project (or consume the package if you publish it internally).
- (Recommended) Add the analyzers to projects that call `IInferService`:

```xml
<ItemGroup>
  <PackageReference Include="ReviDotNet.Analyzers" Version="1.*" PrivateAssets="all" />
</ItemGroup>
```

2) Register ReviDotNet in your DI container

Call `AddReviDotNet()` once from your host's `ConfigureServices` (or `Program.cs`):

```csharp
builder.Services.AddReviDotNet(typeof(Program).Assembly);
```

This registers `IInferService`, `IAgentService`, `IEmbedService`, all registry managers, logging, and a hosted startup initializer that loads your `RConfigs` files at app start.

3) Create minimal configuration files in your app repo

- Provider (`RConfigs/Providers/claude.rcfg`):

```ini
[[general]]
name = claude
enabled = true
protocol = Claude
api-url = https://api.anthropic.com/
api-key = environment
default-model = claude-3-5-sonnet-latest
supports-prompt-completion = true
```

Environment variables for API keys follow: `PROVAPIKEY__CLAUDE` (uppercase, hyphens/spaces to underscores). See `ReviDotNet.Core/Docs/provider-files.md`.

- Model (`RConfigs/Models/Inference/anth_sonnet_35.rcfg`):

```ini
[[general]]
name = anth_sonnet_35
enabled = true
model-string = claude-3-5-sonnet-latest
provider-name = claude

[[settings]]
tier = A
token-limit = 100000
```

- Prompt (`RConfigs/Prompts/Search/analyze-specs.pmt`):

```ini
[[information]]
name = analyze-specs
version = 1

[[settings]]
request-json = false

[[_system]]
You are a helpful assistant.

[[_instruction]]
Analyze the following specs and provide 3 bullet points.

[[_exin_1]]
[Specs]
The system should be fast.

[[_exout_1]]
- Low latency
- Efficient
- Responsive
```

4) Inject and call the inference service

Inject `IInferService` (or `IAgentService` / `IEmbedService`) wherever you need it:

```csharp
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Revi;

public sealed class SpecAnalyzer(IInferService infer)
{
    public async Task RunAsync(CancellationToken token = default)
    {
        List<Input> inputs =
        [
            new Input("Specs", "Users need a clean and fast UI.")
        ];

        // Get raw text
        string? text = await infer.ToString("search/analyze-specs", inputs, token: token);

        // Get a list of strings
        List<string> points = await infer.ToStringList("search/analyze-specs", inputs, token: token);

        // Stream output (concatenate chunks)
        StringBuilder builder = new();
        await foreach (string chunk in infer.CompletionStream("search/analyze-specs", inputs, token: token))
        {
            builder.Append(chunk);
        }

        // Strongly-typed object
        AnalysisResult? result = await infer.ToObject<AnalysisResult>(
            "search/analyze-specs",
            inputs,
            token: token);
    }

    public sealed class AnalysisResult
    {
        public List<string> Points { get; set; } = [];
    }
}
```

Notes

- Prompt names are resolved as: `<lower-cased-subfolder(s)>/<[[information]] name>`; the physical filename is not used for matching. See `ReviDotNet.Core/Docs/analyzers.md`.
- Use `ToEnum<TEnum>` for constrained label tasks; pass `includeEnumValues: true` to inject valid options.
- When `request-json = true` or a guidance schema is enabled, `ToObject<T>` will validate and repair common JSON issues.
- `ToStringListLimited` allows early stop based on count or a custom evaluator.

## Analyzer integration (REVI001, REVI006, REVI007, REVI008)

The `ReviDotNet.Analyzers` package validates prompt and agent usage at compile time.

- `REVI001`: prompt not found in `RConfigs/Prompts`
- `REVI006`: agent not found in `RConfigs/Agents`
- `REVI007`: duplicate effective agent names
- `REVI008`: non-constant agent name in `Agent.Run` / `Agent.ToString` / `Agent.FindAgent`

To enable prompt validation, include your `.pmt` files as `AdditionalFiles` in the projects that compile the calling code:

```xml
<Project>
  <ItemGroup>
    <AdditionalFiles Include="RConfigs\Prompts\**\*.pmt" />
  </ItemGroup>
</Project>
```

If you use agent orchestration, include `.agent` files too so agent-related analyzer rules can run:

```xml
<Project>
  <ItemGroup>
    <AdditionalFiles Include="RConfigs\Agents\**\*.agent" />
  </ItemGroup>
</Project>
```

See `ReviDotNet.Core/Docs/analyzers.md` for details and troubleshooting.

## Configuration & secrets

- Non-secret runtime settings should go through your app’s runtime configuration mechanism (e.g., a `RuntimeConfigService`).
- Provider API keys are read from environment variables when you specify `api-key = environment` in the provider `.rcfg`. See `ReviDotNet.Core/Docs/provider-files.md` for the exact variable naming convention.

## Embeddings

Define embedding model profiles under `RConfigs/Models/Embedding` and use them where vectorization is required. See `ReviDotNet.Core/Docs/model-files.md` for the supported options (`dimensions`, `encoding-format`, `task-type`, etc.).

## Testing

- The `ReviDotNet.Tests` project contains examples and helpers. You can substitute a fake or local provider during tests.
- Consider using CI with analyzers enabled and `-warnaserror+` to keep configuration drift from reaching production.

## Roadmap / Contributions

Open issues or pull requests with:

- Additional analyzer rules (e.g., checking input label mismatches, guidance schema drift)
- New provider protocols
- Samples/tutorials

## License

See `LICENSE.txt` at the repository root of this solution for licensing details pertaining to Revision Labs code.
