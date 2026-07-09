# Forge UI Snapshots

Full-page screenshots of every reachable page in **ReviDotNet.Forge**, captured from a
live local run (dark mode, default `1560px` desktop width, 2× device scale for crispness).

- **Captured:** 2026-07-02
- **Branch:** `feature/refinery-phase-2b-4`
- **How:** headless Edge driving each route on `http://localhost:5000`, waiting for the
  Blazor Server circuit to render, then full-page capture. Data shown is the app's real
  current state (registries loaded from embedded + host-app RConfigs; no MongoDB, so
  Observer shows its empty state).

The **session view** (10 & 11) is the agent event-log / trace view. It is rendered from a
built-in synthetic deep-research run (`/workshop/instance/preview`) that flows through the
same `RlogEvent → view-model` projector a real run uses — so it shows off the event log
without needing a live agent session.

| # | File | Page | Route |
|---|------|------|-------|
| 01 | `01-dashboard.png` | Dashboard — registry counts, live jobs, recent activity, provider health | `/` |
| 02 | `02-observer.png` | Observer — live log feed (empty state; no MongoDB locally) | `/observer` |
| 03 | `03-prompts-registry.png` | Prompts registry | `/prompts` |
| 04 | `04-prompts-workshop.png` | Prompt workshop — create/generate a prompt | `/prompts/new` |
| 05 | `05-test-runner.png` | Prompt test runner | `/test` |
| 06 | `06-optimizer.png` | Prompt optimizer | `/optimize` |
| 07 | `07-agents-registry.png` | Agent registry | `/agents` |
| 08 | `08-agents-new.png` | Create new agent | `/agents/new` |
| 09 | `09-agent-workshop.png` | Agent workshop — sessions & evaluations hub | `/workshop` |
| 10 | `10-session-view-eventlog.png` | **Agent event log / session view** (grouped, collapsed) | `/workshop/instance/preview` |
| 11 | `11-session-view-expanded.png` | **Agent event log / session view** (fully expanded: tool I/O, failures, sub-agent) | `/workshop/instance/preview` |
| 12 | `12-refinery.png` | Refinery — automated agent improvement | `/refinery` |
| 13 | `13-clients.png` | Routing · Clients | `/clients` |
| 14 | `14-models.png` | Routing · Models | `/models` |
| 15 | `15-models-new.png` | Create new model | `/models/new` |
| 16 | `16-providers.png` | Routing · Providers | `/providers` |
| 17 | `17-providers-new.png` | Create new provider | `/providers/new` |
| 18 | `18-inference.png` | Routing · Inference log | `/inference` |
| 19 | `19-embeddings.png` | Routing · Embeddings | `/embeddings` |
| 20 | `20-usage.png` | Usage & cost | `/usage` |
