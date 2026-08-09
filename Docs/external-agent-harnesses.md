# Using External Agent Harnesses from ReviDotNet

Research memo — August 2026.

ReviDotNet ships its own agent harness (`.agent` state machines, `AgentRunner`,
`AgentManager`). This memo evaluates delegating agentic work to *external*
harnesses instead — Claude Code, Codex CLI, Gemini CLI — either through the
Agent Client Protocol (ACP) or by driving the vendor CLIs directly, and covers
what the vendors' terms permit.

**This is engineering research, not legal advice.** Get counsel to read the
actual contracts before shipping a product feature. The Anthropic policy text
quoted below was read directly from Anthropic's own documentation; the dated
enforcement timeline comes from secondary reporting and should be re-verified.

---

## 1. The controlling rule (Anthropic)

Anthropic's [legal and compliance
page](https://code.claude.com/docs/en/legal-and-compliance) is unusually direct.
Verbatim:

> **OAuth authentication** is intended exclusively for purchasers of Claude
> Free, Pro, Max, Team, and Enterprise subscription plans and is designed to
> support ordinary use of Claude Code and other native Anthropic applications.
>
> **Developers** building products or services that interact with Claude's
> capabilities, including those using the Agent SDK, should use API key
> authentication through Claude Console or a supported cloud provider.
> **Anthropic does not permit third-party developers to offer Claude.ai login
> or to route requests through Free, Pro, or Max plan credentials on behalf of
> their users.**
>
> Anthropic reserves the right to take measures to enforce these restrictions
> and may do so without prior notice.

And on limits:

> Advertised usage limits for Pro and Max plans assume ordinary, individual
> usage of Claude Code and the Agent SDK.

The [Agent SDK overview](https://code.claude.com/docs/en/agent-sdk/overview)
repeats it and adds the licensing position:

> Unless previously approved, Anthropic does not allow third party developers to
> offer claude.ai login or rate limits for their products, including agents
> built on the Claude Agent SDK.

> Use of the Claude Agent SDK is governed by Anthropic's Commercial Terms of
> Service, including when you use it to power products and services that you
> make available to your own customers and end users.

Three consequences:

1. **Subscription OAuth is for first-party surfaces only.** Claude Code CLI, the
   VS Code extension, Claude Desktop, claude.ai.
2. **Anything that is a product gets an API key.** Console key, or Bedrock /
   Vertex / Foundry / gateway credentials.
3. **"Unless previously approved" is a real path.** The docs point at
   [contact sales](https://www.anthropic.com/contact-sales) for exactly this
   question, and there is a partner track (the Agent SDK page carries partner
   branding guidelines).

### Branding constraint

If ReviDotNet ever surfaces this in a UI, per the Agent SDK page: "Claude
Agent" and "{YourAgentName} Powered by Claude" are permitted. **"Claude Code"
and "Claude Code Agent" are not**, nor is Claude Code-styled ASCII art or
visual mimicry.

### This is actively enforced

Not theoretical, and the trajectory matters more than the current state:

| Date | Event |
| :--- | :--- |
| Feb 2026 | Terms updated to prohibit subscription OAuth tokens in third-party tools. |
| Feb–Mar 2026 | Server-side blocks rolled out quietly against non-official clients. |
| Apr 4, 2026 | Full enforcement, beginning with OpenClaw and expanding to other harnesses. Blocked calls return `Third-party apps now draw from extra usage, not plan limits.` |
| May 13, 2026 | Announced: Agent SDK, `claude -p`, GitHub Actions and third-party tools move off plan limits onto a separate monthly credit (Pro $20 / Max 5x $100 / Max 20x $200), effective June 15. |
| Jun 15, 2026 | **Cancelled before taking effect.** Programmatic usage keeps drawing from Pro/Max/Team/Enterprise limits as before. Anthropic said it is reworking the plan and will give advance notice. |

The June reversal is why `claude -p` on a subscription still works today. Treat
it as a reprieve with notice attached, not a guarantee. Anything whose unit
economics depend on subscription-priced inference is one email away from a
12x–175x cost change.

---

## 2. Answers to the four questions

### Q1 — Can Claude Code's subagents be the research agent for Great Debate?

Split the question, because the answer differs:

| Scenario | Verdict |
| :--- | :--- |
| You personally run `claude`, `claude -p`, or the Agent SDK on your own machine, on your own Max plan, doing research against the Great Debate repo | **Yes.** Ordinary individual use of a first-party surface. This is the product working as intended. |
| Great Debate the *product* runs research for its users, powered by any Pro/Max subscription (yours or theirs) | **No.** That is a product routing requests through plan credentials. API key + Commercial Terms. |
| Great Debate runs unattended server-side research pipelines on your OAuth token | **Effectively no.** Even single-tenant, this stops being "ordinary, individual usage" once it is a hosted service, and it is exactly the shape enforcement targets. |

The line is **not** whether the repo is private or whether you are the only
human involved. It is whether the traffic is an individual using a first-party
Anthropic surface, versus a service. The private-repo detail is irrelevant to
the terms question.

Practically: developing Great Debate with Claude Code — including using
subagents to do research while you build — is fine. Shipping Great Debate with
Claude Code inside it is an API-key feature.

### Q2 — Same question for ChatGPT / Codex and others

OpenAI has published **no equivalent explicit ban**, but that is not permission:

- Codex CLI is Apache-2.0. A maintainer confirmed on
  [openai/codex#8338](https://github.com/openai/codex/discussions/8338) that you
  may "fork the repo and make modifications to suit your own needs."
- OpenAI's Terms of Use prohibit using "any automated or programmatic method to
  extract data or output from the Services." Written for scraping, never
  publicly reconciled with third-party CLI harnesses.
- OpenAI's own guidance points at API keys for programmatic CLI workflows, SDK
  usage, CI/CD and automation.
- Asked directly and repeatedly whether ChatGPT-plan auth may be used from
  third-party or modified clients, OpenAI maintainers have **not answered**.

So the OpenAI position is ambiguous where Anthropic's is explicit. Worth
remembering that Anthropic's position was *also* ambiguous — with developer
relations reportedly encouraging the pattern — right up until server-side blocks
appeared. Gemini CLI, Cursor and Grok are in the same undefined state.

**Design conclusion:** never let subscription auth be load-bearing for any
ReviDotNet feature, on any vendor. Treat it as a convenience lane that can be
switched off by a vendor at short notice, with an API-key lane underneath.

### Q3 — ACP

One correction, and it is the important one: **T3 Code does not use ACP.** Its
README instructs users to install and authenticate each vendor CLI themselves
(`claude auth login`, `codex login`, `agent login`, `grok login`,
`opencode auth login`); T3 Code then drives those local binaries as
subprocesses. That detail *is* its compliance story — it is a GUI over the
official binary that never touches credentials, and Anthropic reportedly treats
it as compliant for that reason.

ACP itself is real, open, and usable:

- **Agent Client Protocol**, Zed Industries, **Apache-2.0**.
- JSON-RPC 2.0 over stdio; the client launches the agent as a subprocess.
  Capability negotiation, sessions, streaming prompt turns, permission requests
  before sensitive operations, client-provided filesystem and terminal access.
- Generated JSON Schema published in `schema/v1` and `schema/v2`, attached to
  `schema-v*` releases — the intended surface for SDK generators.
- Official SDKs: Rust, TypeScript, Python, Java, Kotlin.
- **Unofficial C# SDK**: [`nuskey8/acp-csharp`](https://github.com/nuskey8/acp-csharp),
  NuGet `AgentClientProtocol`, MIT, client and agent sides.

So yes — ReviDotNet can implement an ACP client, and the protocol layer is
license-clean.

**But ACP is a transport, and transport openness says nothing about auth.**
Compliance depends entirely on what you launch behind it and whose credentials
it uses:

- `@agentclientprotocol/claude-agent-acp` and `@zed-industries/claude-code-acp`
  are built on the **Claude Agent SDK** → Commercial Terms → API key when
  powering a product.
- Launching the user's own already-authenticated `claude` binary → the T3 Code
  model.

### Q4 — "Subscriptions powered by ReviDotNet"

As literally stated — ReviDotNet provisioning, managing, or routing agentic work
through Claude subscriptions on behalf of its users — **this is prohibited**. It
is the exact sentence in the legal page, it has been enforced server-side since
April 2026, and enforcement can happen without notice.

Two adjacent designs are viable:

**BYO-CLI (the T3 Code model).** ReviDotNet runs locally and launches the
`claude` binary the developer authenticated themselves. ReviDotNet never sees,
stores, forwards, or provisions a token. Currently treated as compliant.
Constraints: only works where the CLI runs on the user's own machine — the
`revi` CLI, Forge on a dev box — never a hosted multi-tenant Forge. And it is a
*tolerated pattern*, not a written guarantee.

**API key / cloud provider.** For anything ReviDotNet ships as a product feature
or runs server-side. Unambiguous, and it already fits the existing `.rcfg`
provider model and `PROVAPIKEY__CLAUDE` convention.

One extra wrinkle: ReviDotNet is MIT-licensed and consumed as a library. If it
ships a subscription-auth path, every downstream consumer becomes a third-party
developer routing through plan credentials. The guardrails below matter more
here than they would in a closed application.

---

## 3. Proposed shape for ReviDotNet

### Where it plugs in

`ExecuteCustomToolAsync` in `ReviDotNet.Core/Agents/AgentRunner.cs:975` is still
a stub returning "not yet implemented" for both `Mcp` and `Http`. The `.tool`
format already models a stdio subprocess launch (`server-command`), which is
precisely ACP's transport. The natural extension:

- Add `Acp` to `ToolType` (`Builtin, Mcp, Http, Acp`).
- An `[[acp]]` section reusing the `server-command` shape.
- An `AcpToolExecutor` speaking JSON-RPC 2.0 over the child's stdio, either via
  `AgentClientProtocol` from NuGet or hand-rolled against the published schema.

### Two lanes, made explicit

The single most important design decision is that the two auth models must not
be crossable by accident:

| | Lane A — local / attended | Lane B — hosted / unattended |
| :--- | :--- | :--- |
| Auth | The user's own CLI login | API key / Bedrock / Vertex / Foundry |
| Runs in | `revi` CLI, Forge on a dev machine | Forge-as-a-service, CI, campaigns |
| Terms | Ordinary individual use | Commercial Terms |
| ReviDotNet's role | Control surface | API client |

Make the lane a declared property — e.g. `auth = external-cli | api-key` on the
`.tool` file — rather than something inferred at runtime. Then add a Roslyn
analyzer (the `REVI0xx` infrastructure already exists for exactly this class of
guardrail) that errors when an `external-cli` tool is reachable from an agent
used in a hosted or unattended context, such as a Refinery campaign.

### Credential hygiene for Lane A

Non-negotiable, and worth encoding as tests:

- Never read `~/.claude/.credentials.json` or the macOS Keychain.
- Never set `CLAUDE_CODE_OAUTH_TOKEN` on a child process on a user's behalf.
- Never accept a pasted OAuth token in a `.rcfg` or `.tool` file.
- Launch the vendor binary and let it resolve its own stored login.

The moment ReviDotNet handles the token, it stops being a control surface and
becomes a third-party developer routing plan credentials.

### Lower-effort alternative to ACP

For Claude specifically, `claude -p` is a much cheaper integration than a full
ACP client and maps cleanly onto ReviDotNet's existing structured-output
guidance:

```bash
claude -p "<task>" --output-format stream-json --verbose \
  --json-schema '<schema>' --allowedTools "Read,Grep,WebSearch"
```

`--output-format json` with `--json-schema` returns the parsed object in
`structured_output` — directly analogous to `ToObject<T>`. Subagent activity is
observable: subagent messages carry `parent_tool_use_id`, and
`--forward-subagent-text` surfaces their text and thinking at every nesting
depth, which would feed ReviLogger's trace tree well. `--output-format json`
also reports `total_cost_usd`, which the existing `cost-budget` guardrail could
consume.

Pick ACP instead if the goal is one integration spanning Claude, Codex, Gemini
and OpenCode, plus a permission-request UI in Forge. Pick `claude -p` if Claude
is the near-term target and time matters.

---

## 4. Before shipping any of this

1. **Ask Anthropic in writing.** "Unless previously approved" means approval
   exists as a path, and the docs route this exact question to sales. Do it
   before building Lane A into a product surface, not after.
2. **Do not assume the June 15 reversal is permanent.** Anthropic promised
   advance notice, not permanence. No pricing model should depend on
   subscription-priced programmatic inference.
3. **Have counsel read the actual Commercial and Consumer Terms.**
   `anthropic.com/legal/*` was unreachable from the research sandbox (403 from
   the egress policy), so the quotes above come from Anthropic's documentation
   rather than the contracts themselves.
4. **Respect the branding rules** in any Forge UI.

## Sources

- [Claude Code — Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- [Claude Agent SDK — Overview](https://code.claude.com/docs/en/agent-sdk/overview)
- [Claude Agent SDK — Quickstart](https://code.claude.com/docs/en/agent-sdk/quickstart)
- [Claude Code — Authentication](https://code.claude.com/docs/en/authentication)
- [Claude Code — Run Claude Code programmatically](https://code.claude.com/docs/en/headless)
- [Agent Client Protocol](https://github.com/agentclientprotocol/agent-client-protocol) (Apache-2.0)
- [acp-csharp](https://github.com/nuskey8/acp-csharp) (MIT, NuGet `AgentClientProtocol`)
- [T3 Code](https://github.com/pingdotgg/t3code)
- [openai/codex discussion #8338](https://github.com/openai/codex/discussions/8338)
