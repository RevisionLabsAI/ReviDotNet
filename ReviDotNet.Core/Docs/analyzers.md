# ReviDotNet.Analyzers — Usage Guide

This guide explains how to enable and use the ReviDotNet.Analyzers package, configure rule severities, and troubleshoot common issues. It complements the RConfigs documentation in this folder.

See also:
- prompt-files.md
- provider-files.md
- model-files.md

## What the analyzers do

The analyzers run during compilation and inside the IDE to catch configuration issues early. The initial rule verifies that all prompt names you pass to Revi/Infer APIs actually exist in your repository’s prompt files.

> The analyzers fire on calls to both the static `Infer`/`Agent` classes **and** the injected `IInferService`/`IAgentService` (e.g. `infer.ToString(...)`, `agent.Run(...)`, `ReviClient.Infer/.Agent`). Name parsing mirrors the runtime exactly — values are split on `=` only (not `:`) and quotes are **not** stripped, so write `name = my-prompt` (unquoted).

This is the full set of rules (most are on by default):

- REVI001 — Prompt not found (Error)
  - Ensures a referenced prompt name exists among your `.pmt` files under `RConfigs/Prompts` (any depth).
  - Mirrors the same name resolution that Revi uses at runtime: lower-cased folder prefix + the prompt’s `information.name` declared inside the `.pmt` file. The physical filename is not used for matching.
- REVI002 — Non-constant prompt name (Warning)
  - Warns when a prompt-name argument to an inference method isn't a compile-time constant, so REVI001 can't validate it.
- REVI003 — Prompt input/placeholder mismatch (Error for a missing required input; Warning for an unused input)
  - Cross-checks the `Input` labels you pass against the `{placeholders}` declared in the prompt's `[[_system]]`/`[[_instruction]]`. A missing required placeholder is an **Error**; an input with no matching placeholder is a **Warning**.
- REVI004 — Duplicate prompt name (Warning)
  - Reports when multiple `.pmt` files resolve to the same effective prompt name.
- REVI009 — Unpaired few-shot example (Warning)
  - Flags a `.pmt` file with an `[[_exin_N]]` that has no matching `[[_exout_N]]` (or vice versa). The runtime pairs examples by index and silently drops a half-pair, so an off-by-one or typo in the index would otherwise lose a whole exemplar with no feedback.
- REVI006 — Agent not found
  - Ensures a referenced agent name exists among your `.agent` files under `RConfigs/Agents` (any depth).
  - Uses runtime-equivalent name resolution: lower-cased folder prefix + `[[information]] name` from the `.agent` file.
- REVI007 — Duplicate agent name
  - Reports when multiple `.agent` files resolve to the same effective name.
- REVI008 — Non-constant agent name
  - Warns when `Agent.Run`, `Agent.ToString`, or `Agent.FindAgent` receives a non-constant first argument, which prevents static existence validation.
- REVI040 — Model profile schema (Error/Warning)
  - Validates model `.rcfg` files under `RConfigs/Models` (required `[[general]]` keys; `tier` in A/B/C; numeric `token-limit`; booleans; numeric override-tuning). Also **warns** when an `[[input]]` input type is `listed`/`both` but the `single-item`/`multi-item` templates are missing (which throws at inference).
- REVI041 — Provider profile schema (Error/Warning)
  - Validates provider `.rcfg` files under `RConfigs/Providers` (allowed `protocol` — `OpenAI`, `vLLM`, `Gemini`, `Perplexity`, `LLamaAPI`, `Claude`; required name/api-url; booleans; allowed `default-guidance-type` including the `defer` and bare `json`/`regex`/`gbnf` aliases; non-negative `[[limiting]]` integers).

To enable the schema rules, include your model/provider `.rcfg` files as `AdditionalFiles` (alongside `.pmt`/`.agent`):

```xml
<ItemGroup>
  <AdditionalFiles Include="RConfigs\Models\**\*.rcfg" />
  <AdditionalFiles Include="RConfigs\Providers\**\*.rcfg" />
</ItemGroup>
```

## Installation

The analyzers ship **inside** the `ReviDotNet` package — they are bundled as a Roslyn component and applied automatically to every project that references `ReviDotNet`. There is no separate `ReviDotNet.Analyzers` package to install (the analyzer project is `IsPackable=false`).

```xml
<ItemGroup>
  <PackageReference Include="ReviDotNet" Version="0.1.0" />
</ItemGroup>
```

If you reference `ReviDotNet.Core` as a project reference instead, the analyzer is wired in through Core's `ProjectReference ... OutputItemType="Analyzer"`, so it still runs automatically.

The remaining requirement is to expose your `.pmt`/`.agent` files as `AdditionalFiles` (see below) so the analyzers can read them.

## Required build configuration (AdditionalFiles)

For REVI001 to work, the compiler must receive your prompt files as AdditionalFiles. Add this include to each project that contains code invoking `Infer.*`, or centrally in Directory.Build.props:

```xml
<Project>
  <ItemGroup>
    <!-- Make all prompts visible to analyzers at build/IDE time -->
    <AdditionalFiles Include="RConfigs\Prompts\**\*.pmt" />
  </ItemGroup>
</Project>
```

For agent analyzers (REVI006/REVI007/REVI008), include `.agent` files as AdditionalFiles as well:

```xml
<Project>
  <ItemGroup>
    <AdditionalFiles Include="RConfigs\Agents\**\*.agent" />
  </ItemGroup>
</Project>
```

Notes:
- Use backslashes in MSBuild include paths on Windows. The analyzer normalizes paths internally.
- You can narrow or expand the glob according to your repository layout; the key is that all `.pmt` and `.agent` files reachable by Revi at runtime appear in AdditionalFiles for the project that compiles the calling code.

## Prompt name resolution (what the analyzer checks)

Revi resolves a prompt name by combining:
1) the lower-cased subdirectory path under `RConfigs/Prompts/` with forward slashes and a trailing slash when not empty, plus
2) the value of `[[information]] name = ...` inside the `.pmt` file.

The physical filename does not participate in the name. Example:

```
RConfigs/Prompts/
  Search/
    analyze-specs.pmt       (contains: [[information]] name = analyze-specs)
  Common/
    normalize-input.pmt     (contains: [[information]] name = normalize-input)
```

- The effective names are:
  - "search/analyze-specs"
  - "common/normalize-input"

From C# you must pass these exact names (case-sensitive for the final full name):

```csharp
using Revi;

// OK
var result = await Infer.ToString("search/analyze-specs", new { /* inputs */ });

// Will trigger REVI001 (if no prompt with that effective name exists)
var missing = await Infer.ToString("Search/Analyze-Specs", new { /* inputs */ });
```

## Configuring severity via .editorconfig

You can control the diagnostic severity per repo, directory, or project by setting standard Roslyn keys:

```ini
# .editorconfig
dotnet_diagnostic.REVI001.severity = error   # or warning | suggestion | silent | none
```

- Default severity: Error.
- Setting to `none` effectively disables the rule where this .editorconfig section applies.

## Suppressing a specific occurrence

If you need to intentionally bypass the check in one place, use standard suppression techniques:

```csharp
#pragma warning disable REVI001
var dynamicName = await Infer.ToString(someComputedName, input); // cannot be validated statically
#pragma warning restore REVI001
```

or

```csharp
using System.Diagnostics.CodeAnalysis;

[SuppressMessage("Usage", "REVI001", Justification = "Name computed at runtime only")] 
public Task<string> CallInferAsync(string promptName) => Infer.ToString(promptName, new { /* inputs */ });
```

## Continuous Integration

- Since REVI001 defaults to Error, builds will fail when an invalid prompt name is detected.
- Consider keeping analyzer packages as development-time only (PrivateAssets=all) and running CI with `-warnaserror+` so other warnings don’t get promoted unexpectedly.

## Limitations and scope

- REVI001 validates only string-literal prompt names (e.g., `Infer.ToString("search/analyze-specs", ...)`). If the value is computed or comes from a variable, the analyzer cannot evaluate it and will not report it.
- The analyzer parses `.pmt` files provided via AdditionalFiles to extract `[[information]] name = ...` values. If your file omits this or uses a different field name, the prompt will not be discoverable.
- Folder prefix is derived from the subdirectory path under `RConfigs/Prompts/` and normalized to lowercase with forward slashes. The final, effective name comparison is case-sensitive.

## Troubleshooting

1) "REVI001 fires even though the file exists"
- Ensure the `.pmt` file’s `[[information]]` section has the expected `name =` and that the value matches exactly the string used in code, after applying the lower-cased folder prefix rules.
- Confirm that the project where the error appears includes the `.pmt` file as an AdditionalFile (see Required build configuration above). If you only added AdditionalFiles to a different project, the analyzer in this project cannot see them.
- Verify the path actually contains the `RConfigs/Prompts/` segment. The analyzer only calculates folder prefixes for files under that segment.

2) "Analyzer doesn’t find any prompts"
- Add a temporary build with `-v:n` (normal) and check that AdditionalFiles are listed for the project; or inspect the project file/Directory.Build.props.
- Make sure your glob matches your actual layout (try `RConfigs\Prompts\**\*.pmt`).

3) "We reference prompts by filename"
- The filename is intentionally ignored. Update your code to reference the effective name: `<lower-cased-folder(s)>/<information.name>`.

## Rule reference

### REVI001 — Prompt not found

- Category: Usage
- Default severity: Error
- Triggers when: A string-literal prompt name passed as the first argument to one of the following methods is not found among AdditionalFiles-parsed prompts:
  - `Infer.ToObject`, `Infer.ToEnum`, `Infer.ToString`, `Infer.ToStringList`, `Infer.ToStringListLimited`, `Infer.ToBool`, `Infer.ToJObject`, `Infer.Completion`

Example that produces REVI001:

```csharp
// Assuming there is no RConfigs/Prompts/X/y.pmt with [[information]] name = z such that effective name == "x/z"
string text = await Revi.Infer.ToString("x/z", new { query = "..." });
```

How to fix:
- Create or move a `.pmt` under `RConfigs/Prompts/<folders>/...` with `[[information]] name = <name>` so that the effective name matches the string you pass in code, or
- Update the code to use the correct effective name that already exists.

### REVI009 — Unpaired few-shot example

- Category: Configuration
- Default severity: Warning
- Triggers when: A `.pmt` file declares an `[[_exin_N]]` section without a matching `[[_exout_N]]` (or an `[[_exout_N]]` without a matching `[[_exin_N]]`). Reported at the orphaned section's line.

Example that produces REVI009:

```ini
[[_exin_1]]
hi
[[_exout_1]]
hello
[[_exin_2]]
orphaned — there is no [[_exout_2]]
```

How to fix:
- Add the missing half (`[[_exout_2]]` above), or
- Renumber/remove the stray section so every `_exin_N` has exactly one `_exout_N` with the same index.

### REVI006 — Agent not found

- Category: Usage
- Default severity: Error
- Triggers when: A string-literal agent name passed as the first argument to `Agent.Run`, `Agent.ToString`, or `Agent.FindAgent` is not found among AdditionalFiles-parsed `.agent` files.

How to fix:
- Add or move the target `.agent` file under `RConfigs/Agents/<folders>/...` and set `[[information]] name` so the effective name matches what code passes, or
- Update the code to the correct effective agent name.

### REVI007 — Duplicate agent name

- Category: Usage
- Default severity: Warning
- Triggers when: More than one `.agent` AdditionalFile resolves to the same effective name (`<lower-cased-folder(s)>/<information.name>`).

How to fix:
- Rename one of the conflicting agents, or
- Move one file to a different folder under `RConfigs/Agents` so the resolved names differ.

### REVI008 — Non-constant agent name

- Category: Usage
- Default severity: Warning
- Triggers when: The first argument to `Agent.Run`, `Agent.ToString`, or `Agent.FindAgent` is not a compile-time constant string.

How to fix:
- Prefer string literals or constants for agent names when possible so REVI006 can validate existence at build time.

---

If you need more analyzer rules, open an issue or PR with the desired checks and sample failing/passing snippets.
