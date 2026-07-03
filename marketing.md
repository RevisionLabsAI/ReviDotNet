# ReviDotNet

**Your prompts, models, agents, and tools as version-controlled files — validated at compile time, not in production.**

## What is ReviDotNet?

ReviDotNet is a .NET LLM library built on a simple wager: the things that usually live as scattered C# strings and DI wiring — prompts, providers, models, and agents — should be plain text files you commit, diff, and review like any other code. You author them as `.pmt`, `.rcfg`, and `.agent` files, and roughly 19 Roslyn analyzers check them every time you build, so a misspelled prompt name, a placeholder mismatch, or a broken agent state graph fails the build instead of a 2 a.m. page. You consume it all through a strongly-typed inference API (`ToObject<T>`, `ToEnum`, `ToStringList`, `ToBool`, `CompletionStream`) registered with a single `AddReviDotNet()` call.

## What it can do

**Configuration as data**
- Define prompts in `.pmt` files: identity, version, sampling, system/instruction text, an optional output schema, and few-shot examples in one human-editable file.
- Define providers, inference models, and embedding models in `.rcfg` files — base URL, protocol, API key (resolved from an env var), default model, and rate-limit/retry settings.
- Define agents in `.agent` files: a state-machine loop DSL with per-state models, prompts, tool-gating, guardrails, and explicit USD cost budgets.
- App configs override built-ins by higher version number; on-disk files win, with an embedded-resource fallback.

**Typed inference**
- One named prompt plus a model resolves to a parsed C# result: `ToObject<T>`, `ToEnum<TEnum>`, `ToStringList` / `ToStringListLimited`, `ToBool`, `ToString`, or streamed text via `CompletionStream`.
- Works across OpenAI, Anthropic Claude, Google Gemini, vLLM, and OpenAI-Responses protocols behind one call surface.

**Structured output guidance**
- JSON Schema guidance with `auto` (schema derived from your `T`) and `manual` (hand-authored `[[_schema]]`) modes, injected as provider-native guided-decoding parameters.
- Automatic JSON extraction and repair, plus a `json-fixer` and `enum-fixer` that re-prompt a cheap model to recover malformed output before falling over.
- Regex-constrained decoding on the wire for the vLLM protocol.

**Tier-based routing**
- Prompts request a quality tier (C < B < A) instead of a model name; the engine deterministically picks the lowest enabled model that meets the minimum, honoring preferred/blocked lists. Swap or downgrade models in config without touching C#.

**Native thinking / reasoning**
- Turn on a model's extended-thinking/reasoning with one provider-agnostic `thinking` setting using a five-word common vocabulary (`minimal` < `low` < `medium` < `high` < `max`, plus `none` to disable), so prompts stay portable. Each model maps the word to its provider's wire format — Claude adaptive effort or `budget_tokens`, Gemini `thinkingConfig`, OpenAI `reasoning_effort` — and a prompt can override the model default per request. Claude's reasoning text is returned on `CompletionResult.Thinking`.

**Resilience and safety**
- Transport-level retries with exponential back-off, an application-level output-retry loop, request spacing, and a no-data inactivity watchdog.
- Automatic secret redaction before anything is logged.
- An optional in-process prompt-injection canary: a filter prompt must emit a canary word or the request is rejected.

**Embeddings and retrieval**
- Declare embedding models as `.rcfg` profiles; generate single or batched vectors via `IEmbedService`.
- Built-in cosine / dot-product / Euclidean similarity and top-N semantic search — basic retrieval with no external vector library.

**Web content pipeline**
- Turn a URL (or a bounded crawl) into clean, metadata-tagged, LLM-ready Markdown: Readability extraction, JSON-LD/OpenGraph metadata, heading-aware chunking, and a polite crawler that respects robots.txt and Crawl-delay.
- Reach it via `IWebContentService` or hand it to agents through built-in web-search / web-scrape / web-extract tools.

**Agents**
- Run a configured agent with a single call. The runtime drives the state loop autonomously until a state reaches `[end]`, enforcing a JSON step contract and emitting a full execution trace.
- Guardrails (all optional, all declarative): cycle, step, timeout, tool-call, parallelism, retry, sub-agent-depth, loop-detection, and graceful USD cost budgets that refuse the next call before overspending and exit cleanly with partial output.

**Observability and tooling**
- Rlog structured logging with chainable records, secret redaction, parent/child run correlation, and an optional event sink for a live session viewer.
- A Blazor "Forge" studio and inference gateway: route a consumer app's inference through a remote gateway by dropping in one `forge.rcfg`, with a direct-route escape hatch for latency-sensitive calls.

**Agent evaluation & self-improvement (Refinery)**
- A companion toolkit (`ReviDotNet.Refinery.*` assemblies + the `revi` CLI + a Forge `/refinery` dashboard) that measures and improves your agents. Point it at a trusted local repo that ships a small refinement plugin; it runs your agents against scenario suites, captures each run's execution trace, and scores it on three independent tiers — structural invariant gates, efficiency metrics (tokens / tool-calls / cost / latency), and an Opus-4.8 LLM judge — with pairwise regression comparison and lower-bound statistical aggregation (quality P10, gated-run pass-rate).
- Over iterations it proposes candidate agent/prompt edits (an LLM diff-proposer plus deterministic knob mutators for sampling, guardrails, and system prompts), re-scores each on train + held-out scenarios, and adopts one only if it clears a deterministic regression gate. Promoting an accepted change to your real `.agent`/`.pmt` files is always a separate, human-gated step. Adds calibration reports, LLM scenario generation, dual token budgets, and a deterministic replay mode for CI (`revi test` exits non-zero on a failing case).

## What makes it different

**vs. Semantic Kernel / Microsoft Agent Framework (MAF).** MAF is the stronger choice for large-scale, multi-agent orchestration — graph workflows, checkpointing, time-travel, group-chat and human-in-the-loop are all things ReviDotNet does not have. Where ReviDotNet differs: its config is genuinely declarative end to end. A single `.rcfg` describes providers, inference models, and embedding models; the `.agent` DSL carries states, tool-gating, guardrails, and a native per-agent cost-budget primitive that MAF leaves to your own middleware. And nothing in the Microsoft stack validates prompt/agent/model files at build time — those errors surface at load or run time.

**vs. Microsoft.Extensions.AI (MEAI).** MEAI is the de-facto `IChatClient` / `IEmbeddingGenerator` contract layer, with broader provider reach and a clean middleware pipeline. ReviDotNet does not implement those interfaces today, so it doesn't yet plug into that ecosystem. What it adds on top is the file-and-analyzer story MEAI has no equivalent for — there are no prompt/model files in MEAI to validate — plus batteries-included subsystems (injection canary, web fetch/crawl, json/enum repair) that the Microsoft world tends to push to Azure services.

**vs. LangChain.NET and single-vendor clients (official OpenAI SDK, OllamaSharp, LlmTornado).** These are code-first: prompts, models, and tools are imperative C#, caught only at runtime, with no shared file format. ReviDotNet's whole premise — diffable, PR-reviewable config validated by ~19 analyzers — has no analog here. The honest counterpoint: LlmTornado has far wider provider breadth and mature graph orchestration, and the official OpenAI SDK will always lead on day-one OpenAI feature coverage. ReviDotNet aims to sit one governance-and-correctness layer above clients like these, not out-implement their provider depth.

## How it compares

| Capability | ReviDotNet | SK / MAF | MEAI | LangChain.NET / vendor clients |
|---|---|---|---|---|
| Repo-stored prompt/model/agent files | Yes (`.pmt`/`.rcfg`/`.agent`) | Partial (YAML agents; code-wired providers) | No | No |
| Compile-time Roslyn validation | ~19 analyzers | None | None | None |
| Structured output | JSON auto+manual, regex (vLLM), json/enum repair | JSON-schema only | JSON-schema only | Varies; mostly DIY |
| Tier-based model routing | Built-in | Custom middleware | Custom middleware | No |
| Native reasoning/thinking control | Built-in (5-word vocab → Claude/Gemini/OpenAI) | Provider-specific | Provider-specific | Varies |
| Per-agent USD cost budget | Built-in, declarative | DIY middleware | DIY middleware | No / undocumented |
| In-process injection canary | Yes | Azure Content Safety | No | No |
| Web fetch/crawl pipeline | Built-in | Bing grounding / MCP | No | DIY |
| Embeddings + similarity search | Built-in (in-memory) | Vector-store connectors | Via Extensions.VectorData | Varies |
| Native provider tool/function calling | Not yet | Yes (KernelFunction) | Yes (AIFunction) | Yes (varies) |
| `IChatClient` / MEAI interop | No | Yes | Native | Mostly yes |
| Multi-agent graph orchestration | Single-loop DSL | Strong (graphs, HITL) | Via MAF | LlmTornado: yes |
| Agent eval & regression gating | Refinery toolkit (scenarios, multi-signal scoring, gates) | Via MEAI Evaluation | Evaluation.* suite | promptfoo / DIY |

## Who it is for

- .NET teams building single-agent or small multi-agent LLM apps who want their prompts, models, and agents to live in the repo and go through code review.
- Teams that value shift-left correctness — catching config drift as a build error rather than a production incident.
- Developers who want cloud-agnostic, in-process building blocks (structured output, injection screening, web fetch, embeddings) without standing up Azure services.
- Less suited (today) to large-scale agentic orchestration, deep MEAI ecosystem interop, or apps that depend on native provider tool calling — see Project status.

## Get started

Register with one line of DI:

```csharp
builder.Services.AddReviDotNet();
// then inject IInferService, IAgentService, or IEmbedService
```

Drop a prompt file in your project, e.g. `Prompts/sentiment.pmt`:

```ini
[[information]]
name = sentiment

[[settings]]
min-tier = B
guidance-schema-type = json-auto

[[_system]]
You classify the sentiment of a customer message.

[[_instruction]]
Message: {Message}
```

Call it and get a typed result back — the schema is derived from your `T`:

```csharp
public record Sentiment(string Label, double Confidence);

var result = await infer.ToObject<Sentiment>(
    "sentiment",
    new[] { new Input("Message", "I love this product!") });

Console.WriteLine($"{result.Label} ({result.Confidence:P0})");
```

The standalone, host-less path uses `ReviBuilder.Create()` / `BuildAsync()` to get a `ReviClient` facade instead of DI.

## Project status

ReviDotNet is in active development, and we'd rather be honest than oversell:

- **Custom tools are parse-only.** `.tool` profiles for MCP and HTTP servers are recognized and validated, but dispatch is stubbed (`ExecuteCustomToolAsync` returns "not yet implemented"). In practice agents run the built-in tools (web search/scrape/extract, sub-agent invocation, session-file readers) today; MCP/HTTP execution is pending an MCP client.
- **No native provider tool/function-calling abstraction yet.** This is the most-requested primitive and is on the roadmap.
- **GBNF grammar guidance is declared but not implemented** — `gbnf-auto`/`gbnf-manual` currently resolve to no constraint, so don't rely on them. JSON guidance (and regex on vLLM) is the working path.
- **No MEAI `IChatClient`/`IEmbeddingGenerator` interop** — ReviDotNet is its own surface for now; integrating with the standard contracts is a known priority.
- **Evaluation has grown up — but it lives outside Core.** The Forge studio's prompt optimize/evaluate loop is still single-judge LLM-as-judge, but the new **Refinery** toolkit (`ReviDotNet.Refinery.*`) adds a genuine multi-signal harness: scenario datasets, structural + efficiency + LLM-judge scorers, pairwise regression comparison, statistical aggregation, and deterministic accept/reject gates (CI-usable via `revi test`). The remaining gap is packaging: it ships as a dedicated, agent/trace-oriented toolkit driven through Forge and the `revi` CLI, not as a lightweight `ReviDotNet.Core` evaluation API you can call from arbitrary xUnit tests.
- **No persistent vector store, OpenTelemetry tracing, tokenizer-accurate counting, or config hot-reload yet** — embeddings retrieval is in-memory, observability is via Rlog, token limits use a character-ratio estimate, and config loads once at startup.
- **The README is partially stale** (it under-counts the analyzers and mentions a `.yaml` prompt format that doesn't exist); trust this document and the in-repo docs over it until it's updated.

Everything listed under "What it can do" is confirmed working. The items above are the gaps we're closing — they're listed here so you can decide with eyes open.
