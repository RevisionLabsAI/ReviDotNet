# ReviDotNet.Analyzers — Usage Guide

This guide explains how to enable and use the ReviDotNet.Analyzers package, configure rule severities, and troubleshoot common issues. It complements the RConfigs documentation in this folder.

See also:
- prompt-files.md
- provider-files.md
- model-files.md

## What the analyzers do

The analyzers run during compilation and inside the IDE to catch configuration issues early. The initial rule verifies that all prompt names you pass to Revi/Infer APIs actually exist in your repository’s prompt files.

- REVI001 — Prompt not found
  - Ensures a referenced prompt name exists among your `.pmt` files under `RConfigs/Prompts` (any depth).
  - Mirrors the same name resolution that Revi uses at runtime: lower-cased folder prefix + the prompt’s `information.name` declared inside the `.pmt` file. The physical filename is not used for matching.

## Installation

Add the analyzer package to every project where you call `Revi.Infer.*` methods (or reference a common Directory.Build.props as shown below).

Option A — Per-project reference in your .csproj:

```xml
<ItemGroup>
  <!-- Keep analyzers as PrivateAssets so they don't flow transitively -->
  <PackageReference Include="ReviDotNet.Analyzers" Version="1.*" PrivateAssets="all" />
</ItemGroup>
```

Option B — Centralized reference via Directory.Build.props at your solution root:

```xml
<Project>
  <ItemGroup>
    <PackageReference Include="ReviDotNet.Analyzers" Version="1.*" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

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

Notes:
- Use backslashes in MSBuild include paths on Windows. The analyzer normalizes paths internally.
- You can narrow or expand the glob according to your repository layout; the key is that all `.pmt` files reachable by Revi at runtime appear in AdditionalFiles for the project that compiles the calling code.

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

---

If you need more analyzer rules, open an issue or PR with the desired checks and sample failing/passing snippets.