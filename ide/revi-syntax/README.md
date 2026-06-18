# ReviDotNet config syntax highlighting

A TextMate grammar that highlights ReviDotNet repository-config files:

| Extension | What it is |
| :--- | :--- |
| `.pmt`   | Prompt files (`[[information]]`, `[[settings]]`, `[[tuning]]`, `[[_system]]`, `[[_instruction]]`, `[[_exin_N]]`/`[[_exout_N]]`, `[[_schema]]`) |
| `.rcfg`  | Provider / model / embedding / forge profiles |
| `.agent` | Agent orchestration files (`[[loop]]`, `[[state.X]]`, `[[_loop]]` DSL) |
| `.tool`  | Tool profiles (MCP / HTTP) |

It is packaged in the **VS Code extension layout**, which JetBrains Rider's built-in
**TextMate Bundles** integration reads directly. So the *same* folder works in both editors.

> **Note on "automatic":** the grammar lives in the repo and is shared, but neither Rider nor
> VS Code auto-activates a *custom* language just because the file is checked in. Each developer
> performs the one-time activation below once per machine. After that it is automatic for every
> `.pmt`/`.rcfg`/`.agent`/`.tool` file in any project.

## JetBrains Rider / IntelliJ (one-time, per developer)

1. `Settings/Preferences` → `Editor` → `TextMate Bundles`.
2. Click **+** and select this folder: `ide/revi-syntax`.
3. **OK**. Open any `.pmt`/`.rcfg`/`.agent`/`.tool` file — it is now highlighted.

That's it — Rider reads `package.json` here, registers the `revi` language, and associates the
four extensions automatically from `contributes.languages[].extensions`.

To adjust colors: `Settings` → `Editor` → `Color Scheme` → `TextMate` maps the grammar's scopes
(e.g. `keyword.control`, `entity.name.section`, `variable.parameter`) to your scheme's colors.

## VS Code

- **Quick (per machine):** copy or symlink this folder into your extensions dir
  (`~/.vscode/extensions/revi-syntax-0.1.0`) and reload the window.
- **Packaged:** `npm i -g @vscode/vsce` then `vsce package` in this folder to produce a
  `.vsix`, and `code --install-extension revi-syntax-0.1.0.vsix`.

## What gets highlighted

- `[[section]]` headers — key-value (`entity.name.section`) vs raw `[[_…]]` blocks
  (`keyword.control`) are colored differently.
- `key = value` pairs (split on the first `=`), with `true`/`false`, numbers, and the
  `environment` / `disabled` sentinels picked out in values.
- `# comments` (full-line).
- `{placeholder}` tokens (Filled-input substitution and `single-item`/`multi-item` templates).
- `[Label]` segments inside `[[_exin_N]]` example inputs.
- The `[[_loop]]` agent DSL: state declarations, `-> target`, `[when: SIGNAL]`, `self`, `[end]`.

## Files

```
ide/revi-syntax/
  package.json                  # VS Code manifest + extension→language mapping (Rider reads this too)
  language-configuration.json   # comment char, bracket pairs, auto-closing
  syntaxes/revi.tmLanguage.json # the grammar (scopeName: source.revi)
  README.md
```

The grammar reflects the real parsing rules in `ReviDotNet.Core/Util/RConfigParser.cs`
(double-bracket sections, raw sections begin with `_`, first-`=` split, `#`-comment-at-line-start).
If the file format changes, update `syntaxes/revi.tmLanguage.json`.
