# `.tool` Tool Configuration Files

`.tool` files declare custom tools (MCP or HTTP) that agents can call, alongside the built-in tools (`web-search`, `web-scrape`, `web-extract`, `invoke_agent`). They live under `RConfigs/Tools` and are loaded by the tool registry at startup (disk: `RConfigs/Tools/**/*.tool`; or embedded resources whose name contains `.Tools.` and ends with `.tool`).

> **Status:** `.tool` files are **parsed and registered**, but custom-tool **dispatch is not yet implemented** — at runtime a call to a custom (MCP/HTTP) tool returns a "not yet implemented" result. Today only the built-in tools actually execute. Use this format to prepare configurations; treat execution as forthcoming.

## File Format Overview

Like other ReviDotNet configuration files, `.tool` files use an INI-like structure with `[[section]]` headers and `key = value` pairs.

## Sections and Options

### `[[information]]` (Required)

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `name` | string | (required) | Unique tool name. This is the name agents reference in a state's `tools` list. Loading throws if missing (the file is skipped). |
| `description` | string | `null` | Optional human-readable description. |

### `[[general]]` (Optional)

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `type` | enum | `mcp` | Tool kind. One of `builtin`, `mcp`, `http`. |
| `enabled` | boolean | `true` | Whether the tool is loaded. A profile with `enabled = false` is skipped at load. |

### `[[mcp]]` (Optional)

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `transport` | enum | `stdio` | MCP transport. One of `stdio`, `http`. |
| `server-command` | string | `null` | Command used to launch the MCP server process (for `stdio` transport). |
| `server-url` | string | `null` | Base URL for the MCP server (for `http` transport). |
| `capabilities` | list | (empty) | Comma/space-separated list of MCP tool IDs this profile exposes. |

## Enums

- **`type`** (`ToolType`): `builtin`, `mcp`, `http`. Default `mcp`.
- **`transport`** (`McpTransport`): `stdio`, `http`. Default `stdio`.

## Example (`RConfigs/Tools/filesystem.tool`)

```ini
[[information]]
name = filesystem
description = Local filesystem MCP server

[[general]]
type = mcp
enabled = true

[[mcp]]
transport = stdio
server-command = npx -y @modelcontextprotocol/server-filesystem /data
capabilities = read_file, list_directory
```

To allow an agent state to use it, list the tool name in the state's `tools`:

```ini
[[state.research]]
tools = web-search filesystem
```

See also: [agent-files.md](agent-files.md) (Tool Registration), which covers the built-in tools and how host apps register custom `IBuiltInTool` implementations through `IToolManager`.
