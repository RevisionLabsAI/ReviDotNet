# Architecture

## Project layout

```
ReviDotNet.Forge/
├── Program.cs                     Service wiring, auth, app pipeline
├── appsettings.json               Default config
├── Api/                           HTTP gateway (REST endpoints)
│   ├── ForgeApiEndpoints.cs       Maps /api/v1/* routes
│   ├── ApiKeyAuthMiddleware.cs    X-Forge-ApiKey validation helper
│   ├── ForgeInferRequest.cs       /infer request DTO
│   ├── ForgeInferResponse.cs      /infer response DTO
│   └── ForgeUsageReportRequest.cs Client-side telemetry DTO
├── Components/                    Blazor UI
│   ├── App.razor, Routes.razor
│   ├── Layout/MainLayout.razor    App shell + nav drawer
│   ├── Pages/
│   │   ├── Home.razor             /
│   │   ├── Prompts/               /prompts — registry + editor drawer
│   │   ├── Generate/              /generate — AI-assisted .pmt builder
│   │   ├── Test/                  /test — multi-model test runner
│   │   ├── Optimize/              /optimize — analyze → suggest → revise
│   │   ├── Workshop/              /workshop — agent run/eval/revise
│   │   ├── Observer/              /observer — live log feed
│   │   ├── Usage/                 /usage — gateway traffic dashboard
│   │   └── ApiKeys/               /apikeys — key lifecycle
│   └── Observer/                  Shared log-feed widgets + flattener
├── Services/                      Backend services
│   ├── PromptRegistryService.cs   Reads/writes .pmt files
│   ├── PromptGeneratorService.cs  AI-assisted prompt generation
│   ├── TestRunnerService.cs       Parallel multi-run prompt testing
│   ├── OptimizerService.cs        AnalyzeAsync, GenerateSuggestionsAsync, ReviseStreamAsync
│   ├── ApiKeys/                   IForgeApiKeyService + Mongo impl
│   ├── Gateway/                   GatewayRouterService, ForgeRateLimiterService, UsageDashboardService
│   ├── Mongo/                     ForgeMongoConnectionService
│   ├── Observer/                  IReviLogViewerService + Mongo impl, MongoRlogEventPublisher, ReviLogLimiterService
│   └── Workshop/                  AgentWorkshopService, WorkshopEventBus, BroadcastingRlogEventPublisher
├── Models/
│   ├── ForgeApiKey.cs             Mongo doc for API keys
│   └── ForgeUsageRecord.cs        Mongo doc for gateway traffic
├── RConfigs/                      Bundled prompts, agents, models, providers (embedded resource)
└── wwwroot/                       Static assets
```

The `Docs/` folder you are reading is added by the documentation task; everything else
above already exists in the repo.

## Wiring (Program.cs)

`Program.cs` is short but does several things in sequence:

1. **Configuration** — loads `appsettings.json`, then `appsettings.local.json` (gitignored
   override), then environment variables.
2. **Blazor + MudBlazor** — registers interactive server components and Mud services.
3. **Optional FusionAuth OIDC** — only wires when `Forge:UseAuthentication = true`. Adds
   cookie + OpenID Connect, custom redirect URI handling, and cascading auth state.
4. **Workshop event bus** — singleton in-memory pub/sub for live agent run trace events.
   Registered **before** the publisher because `BroadcastingRlogEventPublisher` resolves
   it.
5. **Revi logging** — picks `MongoRlogEventPublisher` if a Mongo connection string is
   configured, otherwise a no-op `NullRlogEventPublisher`. Either way it wraps it in
   `BroadcastingRlogEventPublisher` so the Workshop UI sees every event live.
6. **`AddReviDotNet(typeof(Program).Assembly)`** — Core's DI extension. Registers all
   registries, hosted-startup initializer (which loads `RConfigs/`), and the inference /
   agent / embed services.
7. **Observer services** — same Mongo/null switch as logging. The viewer is a separate
   service from the publisher.
8. **Forge studio services** — `PromptRegistryService`, `TestRunnerService`,
   `PromptGeneratorService`, `OptimizerService`, `AgentWorkshopService`.
9. **Gateway services** — `IForgeRateLimiterService`, `GatewayRouterService`,
   `UsageDashboardService`.
10. **API key services** — `IMemoryCache` + `IForgeApiKeyService`.
11. **Pipeline assembly** — `ReviServiceLocator.SetProvider(app.Services)` so static
    callers (e.g. `Util.Log`, `AgentReviLogger`) anywhere in the process route into the
    DI publisher. Then static files, auth middleware (if enabled), antiforgery, `/auth/*`
    endpoints (if enabled), `MapForgeApi()`, and `MapRazorComponents<App>()`.

The bridge to `ReviServiceLocator` is important — it means an agent running deep inside
`ReviDotNet.Core` from any thread still publishes structured `RlogEvent`s into the same
Mongo/Workshop bus that the UI is watching.

## Persistence

Forge stores three things in MongoDB when one is configured. The database name defaults
to `BetterNamer` (configurable via `Observer:MongoDb:DatabaseName`).

| Collection | Owner | What it holds |
| --- | --- | --- |
| `LogEvents` | `MongoRlogEventPublisher` (write), `MongoReviLogViewerService` (read) | `RlogEvent` records — every log line from every Revi-instrumented process, including agent step events. |
| `ForgeUsage` | `UsageDashboardService` | One `ForgeUsageRecord` per gateway request — success/failure, latency, tokens, failover attempts, prompt/model/provider. |
| `ForgeApiKeys` | `ForgeApiKeyService` | API key records: SHA256 `KeyHash`, 8-char prefix, ClientId, Enabled, CreatedAt, LastUsedAt. |

When Mongo is unconfigured:

- `MongoRlogEventPublisher` is replaced by `NullRlogEventPublisher` — events still flow
  through the Workshop bus, but nothing is persisted.
- `UsageDashboardService` uses an in-memory ring buffer (~10k entries).
- `ForgeApiKeyService` uses an in-memory list. **Keys do not survive a restart.**

The bundled `RConfigs/` (prompts for the Optimizer, Generator, AgentWorkshop Evaluator
and Reviser; a smoke-test `echo` agent; three provider configs; three model profiles)
are an *embedded resource* in the assembly, but they are also written to disk under the
content root so that user-supplied prompts written through the UI can sit alongside them
on the same path.

## Eventing pipeline

```
Anywhere in process: agent.Run(...), infer.ToObject(...), Util.Log(...)
                                    │
                                    ▼
                          IRlogEventPublisher (DI)
                                    │
                                    ▼
                 BroadcastingRlogEventPublisher  ────────┐
                                    │                    │
                                    ▼                    ▼
                MongoRlogEventPublisher          IWorkshopEventBus
              (channel → batch → Mongo)       (in-memory sub/pub)
                                                         │
                                                         ▼
                                          Workshop UI subscriptions
                                          (per agent-session: tag)
```

The Workshop bus reads each event's `Tags` field, finds an `agent-session:<id>` token,
and fans out to subscribers registered for that session id. This is what lets the
Workshop page show a live, hierarchical trace of an agent run without polling Mongo.

The Observer page, by contrast, **does** poll Mongo (every 2s adaptive, dropping to
500ms when new events appear, climbing back to 2s when idle). That tradeoff exists
because Observer is process-agnostic — it can show events from any instance, not just
runs initiated from this Forge process.

## Gateway request flow

```
HTTP client                Forge                          Provider API
    │                       │                                  │
    │ POST /api/v1/infer   │                                  │
    │ X-Forge-ApiKey: …    │                                  │
    │ ───────────────────► │                                  │
    │                       │ ApiKeyAuth.ValidateAsync         │
    │                       │ (cached lookup → ClientId)      │
    │                       │                                  │
    │                       │ GatewayRouterService             │
    │                       │  ├─ Build candidate list         │
    │                       │  │   (ExplicitModel | Preferred  │
    │                       │  │    | all enabled ≥ MinTier)   │
    │                       │  │                               │
    │                       │  ├─ For each candidate:          │
    │                       │  │   skip if in cooldown         │
    │                       │  │   skip if no InferenceClient  │
    │                       │  │                               │
    │                       │  │   rateLimiter.AcquireAsync ───┐
    │                       │  │   provider.GenerateAsync ─────┼─► OpenAI / Claude / Gemini …
    │                       │  │   release semaphore   ◄───────┘
    │                       │  │                               │
    │                       │  │   on failure → cooldown 60s,  │
    │                       │  │   try next candidate          │
    │                       │  │                               │
    │                       │  └─ Record ForgeUsageRecord      │
    │                       │     (success or final failure)   │
    │                       │                                  │
    │ stream chunks (SSE)  │                                  │
    │ ◄─────────────────── │                                  │
    │   or JSON response   │                                  │
```

The router collects `ForgeUsageRecord`s for both successes and failures, including
streaming runs (per-chunk SSE) and direct-route mode where a client uses the API only to
ask the routing question and then calls the provider itself (`POST /usage/report`).

## ReviDotNet.Core touch points

Forge depends on these abstractions from `Revi`:

| Forge usage | Core type |
| --- | --- |
| List/save prompts | `IPromptManager`, `Prompt`, `Example`, `GuidanceSchemaType` |
| Run inference | `IInferService` (`Completion`, `CompletionStream`, `ToObject<T>`) |
| Test runner timings | `IInferService.CompletionStream` + `Stopwatch` for TTFT |
| List/run agents | `IAgentManager`, `AgentProfile`, `AgentRunner`, `AgentResult`, `AgentExitReason` |
| Workshop event filtering | `AgentReviLogger.Step.*` constants for icon mapping |
| Models | `IModelManager`, `ModelProfile`, `ModelTier` |
| Providers | `Provider.InferenceClient`, `Message`, `StreamingResult<string>` |
| Observability | `Rlog`, `RlogEvent`, `IRlogEventPublisher`, `ReviServiceLocator` |
| Util | `Util.EstTokenCountFromCharCount`, `Util.Log` |

Forge **does not** re-implement any of these. It is a thin UI on top — the heavy lifting
of prompt parsing, model resolution, agent state-machine execution, and provider HTTP
calls happens in Core.
