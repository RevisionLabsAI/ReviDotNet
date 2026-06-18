# Configuration & deployment

## Settings sources

`Program.cs` reads configuration from three layers, last-wins:

1. `appsettings.json` (in repo; safe defaults)
2. `appsettings.local.json` (optional, gitignored — for developer overrides)
3. Environment variables (recommended for secrets and production)

## Key settings

### `Forge:UseAuthentication` (bool, default `false`)

Controls whether FusionAuth OIDC is enabled. When `false`, the UI is fully open to
anyone who can reach the listening port. Set to `true` in any environment that more
than one person can reach.

### `Forge:PromptsSourcePath` (string, default `RConfigs/Prompts`)

Where the **Prompt Registry** and the **Generate Prompt** flow write `.pmt` files.
Used by `PromptRegistryService` for both `Save` (existing prompt edits) and
`SaveNew` (Generate / Optimize accepted revisions).

The path is interpreted relative to the application's working directory at runtime.

### `Revi:RConfigPath` (string, default `RConfigs`)

Where `ReviDotNet.Core`'s `RegistryInitService` looks for `.pmt`, `.agent`,
`.rcfg`, and other config files on startup.

### `Observer:MongoDb:ConnectionString` (string)

A standard Mongo connection URI such as
`mongodb://user:pass@host:27017/?authSource=admin`. **When this is non-empty**, Forge
switches three subsystems from in-memory to Mongo-backed:

- `MongoRlogEventPublisher` — persists every `RlogEvent` to the `LogEvents` collection.
- `MongoReviLogViewerService` — the Observer page can read instances, events, sessions.
- `ForgeMongoConnectionService` — also used by `ForgeApiKeyService` and
  `UsageDashboardService` (Mongo collections `ForgeApiKeys`, `ForgeUsage`).

When empty, all three fall back to no-op / in-memory implementations. **API keys do not
survive a restart in that mode.**

### `Observer:MongoDb:DatabaseName` (string, default `BetterNamer`)

The database name inside the configured Mongo cluster. The default is a placeholder
that reflects Forge's first internal user; change it for your own deployment.

### `FusionAuth:*` (string)

Required only when `Forge:UseAuthentication = true`. Set:

- `FusionAuth:Authority` — base URL of the FusionAuth tenant
- `FusionAuth:ClientId`, `FusionAuth:ClientSecret`
- `FusionAuth:RedirectUri` — the public-facing OIDC callback URL
- `FusionAuth:PostLogoutRedirectUri` — where to send users after logout

The OIDC handler asks for `openid profile email`, names the role claim as `roles`, and
binds `name` to the user-facing name claim.

### Provider API keys (environment-only)

Per Core's convention, each provider's API key is read at startup from
`PROVAPIKEY__<UPPERCASE_PROVIDER_NAME>`. For the three providers bundled in
`ReviDotNet.Forge/RConfigs/Providers`:

- `PROVAPIKEY__OPENAI`
- `PROVAPIKEY__CLAUDE`
- `PROVAPIKEY__GEMINI`

Hyphens and spaces in provider names become underscores when forming the variable name.
See [provider-files.md](../../ReviDotNet.Core/Docs/provider-files.md) for the full rule.

## Client configuration (`forge.rcfg`)

The settings above configure Forge as a **gateway server**. A separate, **client-side** file lets a *consumer* application route its inference through a Forge gateway. It is loaded by Core's `ForgeManager` from `RConfigs/forge.rcfg` (under the app base directory) at startup; when present and enabled, `IInferService.Completion`/`CompletionStream` route remotely through the gateway instead of calling providers directly (see [inference.md → Forge Gateway Routing](../../ReviDotNet.Core/Docs/inference.md)).

```ini
[[general]]
enabled = true
forge-url = https://forge.example.com
api-key = environment
client-id = my-app
timeout-seconds = 300
```

| Key (under `[[general]]`) | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `enabled` | boolean | — | Must be `true` for routing to activate. |
| `forge-url` | string | — | Base URL of the Forge gateway. Required; routing is skipped if empty. |
| `api-key` | string | — | The gateway API key. Use `environment` to load it from the `FORGE_API_KEY` environment variable. |
| `client-id` | string | `unknown` | Identifier reported with usage. |
| `timeout-seconds` | integer | `300` | Request timeout for gateway calls. |

> Routed calls bypass the **local** prompt-injection `filter`, model selection, and retry pipeline — the gateway owns those. Use the `directRoute` parameter on the `Prompt`-object inference overloads to force a local call.

[`RConfigs/`](../RConfigs) is included as an **embedded resource** in the assembly and
also lives on disk under the project content root. It ships:

| Path | Purpose |
| --- | --- |
| `Providers/openai.rcfg`, `claude.rcfg`, `gemini.rcfg` | The three model providers Forge knows how to call out of the box. Each declares `protocol`, `api-url`, and `api-key = environment` so the runtime pulls the secret from `PROVAPIKEY__*`. |
| `Models/gpt-4o-mini.rcfg`, `claude-3-5-sonnet.rcfg`, `gemini-1-5-flash.rcfg` | Three model profiles wired to those providers. Each has a `tier` so the gateway's `MinTier` filter has something to act on. |
| `Prompts/Optimizer/Generator.pmt` | Used by **Generate Prompt** to produce new `.pmt` files. |
| `Prompts/Optimizer/Analyzer.pmt` | Used by **Test Runner** and **Optimizer**'s analysis tab to score individual runs. |
| `Prompts/Optimizer/Suggester.pmt` | Used by **Optimizer** to aggregate analyses into a ranked suggestion list. |
| `Prompts/Optimizer/Reviser.pmt` | Used by **Optimizer**'s Apply & Iterate tab to produce a new `.pmt`. |
| `Prompts/Optimizer/SimpleTask.pmt` | Reference / example prompt — left over from the original Optimizer console app. |
| `Prompts/AgentWorkshop/Evaluator.pmt` | Used by **Agent Workshop** Evaluation tab to score one or more sessions and produce ranked recommendations + alternatives. |
| `Prompts/AgentWorkshop/Reviser.pmt` | Used by **Agent Workshop** when a user clicks **Generate Diff** on a recommendation — produces the full revised `.agent`. |
| `Agents/test/echo.agent` | Smoke-test agent that echoes its input back and signals DONE. Used to verify Workshop end-to-end without depending on a real task. |

User-supplied prompts and agents live in the same tree — the runtime makes no
distinction between bundled and added.

## Deployment shape

The `.csproj` declares `IsPackable=false` with the comment "Web app — distributed as
Docker image, not a NuGet package." A typical deployment:

1. `docker build` the project (a standard `dotnet publish`-based Dockerfile).
2. Mount a volume at `<content-root>/RConfigs/` so user-authored prompts and agents
   survive container redeploys.
3. Configure environment variables for:
   - `Observer__MongoDb__ConnectionString` (recommended)
   - `Observer__MongoDb__DatabaseName` (recommended; change from the default)
   - `Forge__UseAuthentication=true` plus the FusionAuth quartet (recommended for any
     shared environment)
   - `PROVAPIKEY__<NAME>` for each enabled provider
4. Front the container with reverse proxy (the gateway is HTTP; SSE requires the proxy
   not to buffer responses — e.g., `proxy_buffering off;` in nginx).

## Development setup

For local dev:

1. Clone the repo. Build the solution with `dotnet build`.
2. Set `PROVAPIKEY__OPENAI` (or whichever provider you'll use) in your shell.
3. Run `dotnet run --project ReviDotNet.Forge`. The default port is whatever
   `launchSettings.json` declares.
4. Browse `https://localhost:<port>/`.

The app boots cleanly with no Mongo, no FusionAuth, and one valid provider key. All
studio features work; Observer shows nothing (no events persisted); API key persistence
is in-memory.

## Authentication behavior

When `Forge:UseAuthentication = true`:

- All Razor pages require an authenticated user. Unauthorized requests redirect to
  `/auth/login?redirectUrl=<original>`.
- `/auth/login` issues an OIDC challenge to FusionAuth.
- `/auth/logout` clears the cookie and signs the user out at FusionAuth (passes the
  `id_token` as `IdTokenHint`).
- The `/api/v1/*` gateway endpoints do **not** participate in OIDC — they validate the
  `X-Forge-ApiKey` header regardless of the global auth setting. This is intentional:
  the gateway is for service-to-service calls; the UI is for humans.
