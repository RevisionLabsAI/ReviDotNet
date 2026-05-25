# Roadmap and observed gaps

This is a list of things the codebase suggests are planned, half-built, or obvious
next steps. It's not a product backlog — it's a snapshot of what a careful read of
the source surfaced. Items are grouped by area.

## Dashboard

The Home page has two stat cards (Generate Prompt, Optimizer) with em-dashes for the
value:

```razor
<MudText Typo="Typo.h4" Class="mt-2 mb-1 opacity-60">—</MudText>
```

These look like placeholders for "prompts generated this session" or "revisions applied
this week" — easy to populate once telemetry exists.

## Prompt Registry

The Edit drawer only exposes **System**, **Instruction**, and **Schema**. To change
`[[settings]]` (e.g., `request-json`, `chain-of-thought`) or `[[tuning]]` parameters
(temperature, top-k, etc.) a user has to edit the file directly or regenerate the
prompt. Adding tuning controls to the drawer is a small, contained change.

There is no way to see or jump between *versions* of a prompt in the registry — only
the most recent on disk. Version history relies entirely on source control.

## Optimizer

- The Reviser prompt structurally only revises `[[_system]]` and `[[_instruction]]`,
  but the Suggester labels its suggestions as affecting one of
  `system / instruction / schema / settings / tuning`. Suggestions targeting
  `schema / settings / tuning` are passed through but not acted on.
- There is no held-out evaluation set. Analysis and revision use the same inputs, which
  invites over-fitting.
- The Optimizer doesn't run a regression check after **Accept & Save** — the user is on
  the hook to re-run the Test Runner.
- The class file `ReviDotNet.Core/Optimization/Optimization.cs` is a much more
  ambitious legacy optimizer with passes for Base, Reflection, and Combo optimization,
  followed by Parameter Tuning. It is currently full of `TODO` comments and not wired
  to the UI. The Forge `OptimizerService` is a much simpler 3-step replacement that
  takes a different approach.

## Agent Workshop

- The Workshop Run tab has a "Workshop event bus" subscription, but if the user
  navigates away mid-run the events still fire — there's no warning that the
  subscription is alive.
- `SaveAgentRevisionAsync` cannot save embedded-resource agents. There's no UI
  affordance to explain that bundled agents are read-only; the call simply throws.
- Workshop assumes MongoDB is configured — without it, the evaluator can't
  reconstruct activity logs across runs even though the in-process Workshop bus would
  technically have them. A future improvement would be falling back to the bus's
  in-memory event store when Mongo is absent.

## Gateway

- `ForgeRateLimiterService` rate-limits per provider but not per client. Adding a
  per-client (or per-key) quota is a natural extension once usage metering is good
  enough to enforce it.
- The 60-second model cooldown is fixed. Providers with longer / shorter outage
  patterns benefit from per-model configurable cooldowns.
- The Gateway Router never *re-tries* a single model — failure goes straight to
  failover. Idempotent retries on 429 / 5xx could reduce unnecessary failovers.
- The gateway doesn't enforce that `ClientId` in the body matches the `ClientId` bound
  to the API key by the auth middleware. A client could send any string as `ClientId`.
  The middleware already stores the validated `ClientId` in `HttpContext.Items` — it's
  not currently consulted at record time.

## Observer

- The Observer page is process-agnostic, but there's no way to *jump from a Workshop
  run to its Observer view* — the session id and instance id are both known, so a
  deep link would close that loop.
- "Clear Logs" supports All / >1 hour / >1 day. Custom cutoffs (e.g., older than a
  user-picked date) aren't supported.
- The suppression rule file (`revilogger_limiter.txt`) is in plain text at the solution
  root or app base directory — no UI yet for sharing rules between developers (e.g.,
  committing them as part of `RConfigs/`).

## API Keys

- The "Generate New Key" dialog asks for a Client ID through a raw HTML `<input>`
  inside a `ShowMessageBox`, then ignores it and hard-codes `_newClientId = "NewClient"`.
  This is clearly an unfinished implementation; the actual UX should be a proper form.
- `CopyNewKey` has the JS clipboard interop commented out as a future task.
- There's no audit trail of who created or revoked a key (no admin user attribution),
  because the API Keys page doesn't read the auth context.

## Cross-cutting

- **No Providers / Models management UI.** Adding a model is a file-edit + restart
  operation. Even read-only listing would be nice.
- **No global "current user" badge** in the layout, even when OIDC is enabled.
- **No theme persistence.** Light/dark is reset every refresh.
- **No tests under `ReviDotNet.Forge`** — the test project (`ReviDotNet.Tests`) covers
  Core but not Forge's services. Adding tests for `PromptRegistryService` serialization,
  `GatewayRouterService` candidate selection, and `ForgeApiKeyService` validation would
  be high-value.
