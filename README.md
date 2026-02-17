# ReviDotNet

ReviDotNet is a .NET library that makes working with modern LLMs straightforward, safe, and configurable. It separates prompt logic, provider connections, and model settings into simple repository files, adds resilience (retries, validators, output fixers), and provides Roslyn analyzers to catch mistakes at build time.

## Features

- Configuration-as-files (kept in your repo)
  - Prompts: `.pmt` files with sections for `[[information]]`, `[[settings]]`, `[[tuning]]`, raw prompt content, and examples
  - Providers: `.rcfg` files describing API host, keys, and protocol per provider
  - Models: `.rcfg` files describing model profiles (limits, defaults, overrides)
- Multiple providers and models with selection/tiering (`A`/`B`/`C`) and per-prompt overrides
- Chat and prompt completion interfaces (automatically chosen or forced per prompt/model)
- Structured output guidance options (JSON/Regex/GBNF – manual and auto variants)
- Resilience: retries, timeout control, token accounting, safe JSON extraction from Markdown
- Built‑in fixers for common issues (e.g., `json-fixer` and `enum-fixer` prompts)
- Input filtering and optional safety canary to detect injection attempts
- Simple, strongly-typed inference API: `ToObject<T>`, `ToEnum<TEnum>`, `ToStringList`, `ToStringListLimited`, `ToBool`, `ToString`, streaming via `CompletionStream`
- First-class Roslyn analyzers (REVI001) to validate prompt names at compile time
- Embeddings support via model profiles dedicated to embeddings

## Repository layout (ReviDotNet)

- `ReviDotNet.Core` – main runtime (config parsing, providers, models, inference API)
- `ReviDotNet.Analyzers` – Roslyn analyzers (e.g., REVI001: prompt not found)
- `ReviDotNet.Tests` – unit tests and helpers

Your app’s configuration files typically live under an `RConfigs` folder in your project:

- `RConfigs/Prompts` – `.pmt` prompt files (any subfolders)
- `RConfigs/Providers` – provider `.rcfg` files
- `RConfigs/Models/Inference` – inference model `.rcfg` files
- `RConfigs/Models/Embedding` – embedding model `.rcfg` files

## Documentation

The core docs are in this repo:

- Prompt files: `ReviDotNet.Core/Docs/prompt-files.md`
- Provider files: `ReviDotNet.Core/Docs/provider-files.md`
- Model files: `ReviDotNet.Core/Docs/model-files.md`
- Inference API: `ReviDotNet.Core/Docs/inference.md`
- Analyzers: `ReviDotNet.Core/Docs/analyzers.md`

## Quick start

1) Add ReviDotNet to your solution

- Reference the `ReviDotNet.Core` project (or consume the package if you publish it internally).
- (Recommended) Add the analyzers to projects that call the `Infer.*` API:

```xml
<ItemGroup>
  <PackageReference Include="ReviDotNet.Analyzers" Version="1.*" PrivateAssets="all" />
</ItemGroup>
```

2) Create minimal configuration files in your app repo

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

3) Call the API from C#

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Revi;

public static class Demo
{
    public static async Task RunAsync(CancellationToken token = default)
    {
        List<Input> inputs =
        [
            new Input("Specs", "Users need a clean and fast UI.")
        ];

        // Get raw text
        string? text = await Infer.ToString("search/analyze-specs", inputs, token: token);

        // Get a list of strings
        List<string> points = await Infer.ToStringList("search/analyze-specs", inputs, token: token);

        // Stream output (concatenate chunks)
        StringBuilder builder = new StringBuilder();
        await foreach (string chunk in Infer.CompletionStream("search/analyze-specs", inputs, token: token))
        {
            builder.Append(chunk);
        }

        // Strongly-typed object
        AnalysisResult? result = await Infer.ToObject<AnalysisResult>(
            "search/analyze-specs",
            inputs,
            modelName: null,
            token: token);
    }

    public sealed class AnalysisResult
    {
        public List<string> Points { get; set; } = new List<string>();
    }
}
```

Notes

- Prompt names are resolved as: `<lower-cased-subfolder(s)>/<[[information]] name>`; the physical filename is not used for matching. See `ReviDotNet.Core/Docs/analyzers.md`.
- Use `ToEnum<TEnum>` for constrained label tasks; pass `includeEnumValues: true` to inject valid options.
- When `request-json = true` or a guidance schema is enabled, `ToObject<T>` will validate and repair common JSON issues.
- `ToStringListLimited` allows early stop based on count or a custom evaluator.

## Analyzer integration (REVI001)

The `ReviDotNet.Analyzers` package validates that string-literal prompt names passed to `Infer.*` methods exist in your `RConfigs/Prompts` tree. To enable name discovery at build time, include your `.pmt` files as `AdditionalFiles` in the projects that compile the calling code:

```xml
<Project>
  <ItemGroup>
    <AdditionalFiles Include="RConfigs\Prompts\**\*.pmt" />
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
