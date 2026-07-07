# Overview

## What Forge is

Forge is a single Blazor Server application that bundles three roles that would otherwise need
separate tools:

| Role | What it does | Surfaces |
| --- | --- | --- |
| **Prompt engineering studio** | Author, test, score and iteratively improve `.pmt` prompt files and `.agent` agent files. | Pages: Prompt Registry, Generate, Test Runner, Optimizer, Agent Workshop. |
| **Observability console** | Live log feed for any process emitting ReviLog events — local dev runs, deployed services, and Forge's own gateway. | Page: Observer. Backed by MongoDB. |
| **Inference gateway** | A small authenticated HTTP API that other applications call instead of talking to OpenAI / Anthropic / Google directly. Forge handles routing, failover, rate limiting, and usage accounting. | Endpoints: `POST /api/v1/infer`, `POST /api/v1/usage/report`, `GET /api/v1/prompts`, `GET /api/v1/models`. Pages: Usage, API Keys. |
| **Refinery host** | Builds and loads refinement plugins from trusted local repos, then runs measurement/improvement campaigns against their agents — scenario suites, invariant + efficiency + LLM-judge scoring, regression-gated variant proposals, and human-gated promotion. | Page: `/refinery`. API: `/api/refinery/*` (consumed by the `revi` CLI). |

The Refinery role sits slightly apart from the other three: it is a reusable toolkit
(`ReviDotNet.Refinery.Sdk` / `.Refinery` / `.Refinery.Hosting`) that Forge merely hosts.
Forge wires the engine into DI, points it at plugin repos via `Refinery:Repos`, and
exposes the `/refinery` dashboard plus the `/api/refinery` Control API — the same API the
standalone [`revi` CLI](revi-cli.md) drives. Campaign runs execute through the same Core
inference/agent services as everything else, so their traces show up in Observer like any
other run. See [features.md](features.md#refinery-refinery),
[refinery-campaigns.md](refinery-campaigns.md), and
[refinery-plugin-authoring.md](refinery-plugin-authoring.md).

The roles share configuration (`RConfigs/`), the in-memory Revi registries
(`IPromptManager`, `IModelManager`, `IProviderManager`, `IAgentManager`, `IToolManager`),
and the ReviLog event publishing pipeline. That is why they are bundled: a prompt the
engineer edits in the Registry is the same prompt the gateway can serve, and every run
through any of the screens emits ReviLog events that show up in Observer.

## Who it is for

- **Prompt engineers / AI engineers** who write `.pmt` and `.agent` files and want a
  shortcut between "I think this prompt has a problem" and "here is a versioned
  replacement that scores better."
- **Application developers** who consume LLMs through the Forge gateway instead of writing
  provider-specific HTTP code. They get key management, automatic failover, and usage
  dashboards "for free."
- **Operators / on-call** who want a single screen showing what a deployed service is
  doing right now — Observer ties together logs, agent sessions, and gateway traffic.

## The shape of a Forge install

```
┌──────────────────────────────────────────────────────────────────┐
│                       ReviDotNet.Forge (Blazor)                  │
│                                                                  │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐   │
│  │  Studio UI  │  │  Observer   │  │  Gateway API (/api/v1)  │   │
│  │ (prompts,   │  │ (log feed,  │  │  + ApiKey middleware    │   │
│  │  agents,    │  │  filters,   │  │  + GatewayRouter        │   │
│  │  optimizer) │  │  limiter)   │  │  + RateLimiter          │   │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘   │
│         │                │                       │              │
│         ▼                ▼                       ▼              │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │     ReviDotNet.Core: IInferService, IAgentService,        │  │
│  │     IPromptManager, IModelManager, IAgentManager…         │  │
│  └───────────────────────────────────────────────────────────┘  │
│                          │                                       │
│        ┌─────────────────┼─────────────────┐                     │
│        ▼                 ▼                 ▼                     │
│   RConfigs/         WorkshopEventBus    BroadcastingRlog        │
│   on disk          (live in-memory)     Publisher → Mongo        │
└──────────────────────────────────────────────────────────────────┘
                                   │
                ┌──────────────────┼─────────────────┐
                ▼                  ▼                 ▼
        MongoDB (optional)    FusionAuth (opt)    OpenAI/Claude/...
        - LogEvents                                (provider APIs)
        - ForgeUsage
        - ForgeApiKeys
```

Each external dependency is optional. When MongoDB is not configured, Observer falls back
to a no-op viewer, the gateway uses an in-memory ring buffer for usage records, and the
API key store falls back to an in-memory list (useful for local development; not durable
across restarts). When FusionAuth is not configured, the UI runs unauthenticated — which
is again fine for local development but should be turned on for any deployment that more
than one person can reach.

## What it is *not*

- **Not a hosted SaaS.** Forge is shipped as a project you build into a Docker image and
  run inside your own environment. There is no multi-tenant separation; every API key in
  one Forge install can see every prompt and model in that install.
- **Not a replacement for `ReviDotNet.Core`.** Forge depends on Core; it does not
  re-implement prompt parsing, model resolution, or agent execution.
- **Not a public-internet endpoint.** The gateway authenticates with a static
  `X-Forge-ApiKey` header. There is no per-request rate limit by client or quota system;
  the only rate limiting is a per-provider concurrency cap to protect upstream APIs.
