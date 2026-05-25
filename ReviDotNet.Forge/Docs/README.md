# ReviDotNet.Forge

ReviDotNet.Forge is the operations and prompt-engineering web application for the ReviDotNet
LLM framework. It is a Blazor Server (.NET 9) app, built on MudBlazor, distributed as a Docker
image, and acts as the human-facing console plus an internal LLM proxy for everything else in
a ReviDotNet deployment.

The `ReviDotNet.Core` library handles prompt parsing, model routing, agent orchestration, and
inference. Forge is the place where humans interact with that library: authoring prompts,
testing and improving them, running and tuning agents, watching live traces, and serving
inference to other applications over an authenticated HTTP gateway.

## Table of contents

- [Overview](overview.md) — what Forge is, who it is for, and the three big jobs it does
- [Architecture](architecture.md) — services, persistence, eventing, and how requests flow
- [Feature reference](features.md) — every page and endpoint, in detail
- [User flows](user-flows.md) — step-by-step walkthroughs for the main jobs Forge supports
- [Configuration & deployment](configuration.md) — `appsettings.json`, environment variables,
  Mongo, FusionAuth, Docker
- [Roadmap](roadmap.md) — stubs, TODOs, and obvious next steps observed in the codebase

> These documents were produced by analyzing the `ReviDotNet.Forge` project against
> `ReviDotNet.Core`. They describe what the codebase does and is intended to do — they do
> not constitute a binding product spec.
