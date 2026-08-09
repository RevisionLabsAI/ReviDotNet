# Using External Agent Harnesses from ReviDotNet

Research memo — August 2026.

ReviDotNet ships its own agent harness (`.agent` state machines, `AgentRunner`,
`AgentManager`). This memo evaluates delegating agentic work to *external*
harnesses instead — Claude Code, Codex CLI, Gemini CLI — either through the
Agent Client Protocol (ACP) or by driving the vendor CLIs directly, and covers
what the vendors' terms permit.

**This is engineering research, not legal advice.** Get counsel to read the
actual contracts before shipping a product feature. Everything quoted below
comes from Anthropic's and OpenAI's own documentation and on-record statements,
except where marked. `anthropic.com/legal/*` remained unreachable from the
research sandbox, so the Consumer and Commercial Terms themselves were not read
first-hand.

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

### What actually makes something a "third-party tool"

This is the distinction that decides most real designs, and it is **not** about
whether there is a GUI, a web front-end, or automation involved. It is about
**which process makes the model request**:

| | Who calls the API | Status |
| :--- | :--- | :--- |
| OpenCode, Pi, OpenClaw (pre-block) | The tool's own code, carrying the user's OAuth token to `api.anthropic.com` | Third-party tool. Blocked from plan limits since Apr 4, 2026. |
| T3 Code, Conductor, any CLI wrapper | The official `claude` binary, which authenticates itself | Not a third-party tool. Never blocked. |

Anthropic's spokesperson [to *The
Register*](https://www.theregister.com/2026/04/06/anthropic_closes_door_on_subscription/),
April 2026:

> Starting April 4, third-party tools will draw from extra usage instead of
> subscription limits. Using Claude subscriptions with third-party tools isn't
> permitted under our Terms of Service, and they put an outsized strain on our
> systems. […] **Claude subscriptions continue to apply to Claude.ai, Claude
> Code, and Cowork.**

Note that enforcement is *billing-based*, not a hard block — offending traffic is
redirected to paid extra usage at API rates. Note also the stated motive:
capacity, not principle. Boris Cherny, head of Claude Code: "Our systems are
highly optimized for one kind of workload." OpenClaw — explicitly designed to
"operate autonomously 24/7" — is the shape that triggered it. **Usage pattern is
the real risk axis, not the presence of a front-end.**

If your code shells out to the official binary and never touches a token, the
requests *are* Claude Code usage and the subscription covers them.

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
| Jun 15, 2026 | **Paused before taking effect.** |

Verbatim, from the banner now at the top of [Use the Claude Agent SDK with your
Claude plan](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)
(article dated June 16, 2026):

> **Update June 15:** We're pausing the changes to Claude Agent SDK usage
> described below. For now, nothing has changed: Claude Agent SDK, `claude -p`,
> and third-party app usage still draw from your subscription's usage limits.
> The previously announced monthly credit […] isn't available. We're working to
> update the plan to better support how users build with Claude subscriptions.
> When we have an update, we'll share it before anything takes effect.

The same article states the boundary Anthropic draws around subscription-backed
programmatic use:

> **Production automation at scale.** The Agent SDK monthly credit is sized for
> individual experimentation and automation. Teams running shared production
> automation should use Claude Platform with an API key for predictable
> pay-as-you-go billing.

"Individual experimentation and automation" versus "shared production
automation" is the line that matters, and it is drawn at *shared*, not at
*automated*.

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
| You personally run `claude`, `claude -p`, or the Agent SDK on your own machine, on your own Max plan | **Yes.** Ordinary individual use of a first-party surface. |
| A **single-user front-end you built for yourself** that shells out to the official `claude` binary, which authenticates with your own login | **Yes.** The requests originate from Claude Code, so the subscription covers them. This is the T3 Code model. |
| The same front-end, but your code carries the OAuth token and calls `api.anthropic.com` itself | **No.** That is the blocked third-party-tool pattern regardless of how many people use it. |
| Great Debate opened to other users, with their work powered by any Pro/Max subscription | **No.** "On behalf of their users" activates. API key + Commercial Terms. |

The distinctions that matter are **who makes the request** and **how many
people use the front-end**. Whether the repo is private, whether there's a web
UI, and whether the run is attended are not, on their own, the deciding facts.

So the design you described — a web front-end, used only by you, that exists to
make the CLI harness more usable for your task — sits inside the sanctioned
envelope, provided the official binary makes every model call. Anthropic's own
framing for it is "individual experimentation and automation."

**The output being for other people is a non-issue for the auth question.**
Nothing in the terms, the Usage Policy, or the [agentic usage
guidance](https://support.claude.com/en/articles/12005017-using-agents-according-to-our-usage-policy)
restricts publishing or distributing what Claude produces for you. Outputs are
yours to use.

One content-side flag, given the project's subject matter: that agentic
guidance does prohibit using agents to "automate influence operations or
coordinated inauthentic behavior," and to "manipulate online polls, voting
systems, or traffic metrics." Research briefs and debate preparation are
nowhere near that line. Mass-generating persuasion content posted under
personas that conceal its origin would be. Keep AI involvement disclosed where
it matters and this stays a non-issue — but it is a *content* constraint, wholly
separate from the auth question, and it applies identically whether you use a
subscription or an API key.

### Q2 — Same question for ChatGPT / Codex and others

For the **drive-the-CLI** design, Codex is on at least as good footing as Claude,
arguably better, because OpenAI documents the scripted path directly:

- [Non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode) is
  an official, documented feature: "Non-interactive mode lets you run Codex from
  scripts (for example, continuous integration (CI) jobs) without opening the
  interactive TUI. You invoke it with `codex exec`." It supports
  `codex exec --json` JSON Lines output with typed items for agent messages,
  reasoning, command executions, file changes, MCP tool calls and web searches —
  a good match for ReviLogger's trace tree.
- [Authentication](https://developers.openai.com/codex/auth) lists both methods
  for Codex CLI without restricting either to interactive use: "Sign in with
  ChatGPT for subscription access" and "Sign in with an API key for usage-based
  access."
- Codex CLI is Apache-2.0; a maintainer confirmed on
  [openai/codex#8338](https://github.com/openai/codex/discussions/8338) that you
  may "fork the repo and make modifications to suit your own needs."
- **No published prohibition** on third-party harnesses or on plan-authenticated
  automation — nothing equivalent to Anthropic's legal page.

The residual ambiguity is narrower than it first appears. OpenAI's Terms of Use
bar "any automated or programmatic method to extract data or output from the
Services," but that clause is aimed at scraping and cannot coherently forbid
`codex exec`, which OpenAI itself ships and documents for CI. What has never
been answered is whether you may *distribute* a tool that other people use with
their own ChatGPT plans — asked repeatedly, unanswered. That is the same
boundary Anthropic drew explicitly.

Anthropic's position was also ambiguous — with developer relations reportedly
encouraging the pattern — right up until server-side blocks appeared, and the
driver was capacity rather than principle. Google enforced similarly against
piggybacking on Gemini CLI's OAuth in February 2026. Assume every vendor's
subscription lane can narrow on short notice.

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

**BYO-CLI (the T3 Code model).** ReviDotNet launches the `claude` binary that
the operator authenticated themselves, and the binary makes every model call.
Currently treated as compliant, and it covers single-user hosted deployments,
not just localhost: a headless box authenticating with a `claude setup-token`
OAuth token is officially supported — it is exactly what the [Claude Code GitHub
Action](https://code.claude.com/docs/en/github-actions) does, where
`CLAUDE_CODE_OAUTH_TOKEN` is "an OAuth token that authenticates with your Claude
subscription" and "runs use your Claude subscription instead of API billing."
Anthropic notes the token "is tied to the subscription of the person who ran
`claude setup-token`," which is precisely why this scales to one person and no
further. Constraint: one operator, one subscription. A second user makes it a
multi-tenant service. And it is a *tolerated pattern*, not a written guarantee.

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

The load-bearing rule is **the official binary makes every model request**.
ReviDotNet may launch it, feed it a prompt, and parse its output; ReviDotNet may
not talk to `api.anthropic.com` with a plan credential. Worth encoding as tests:

- Never read `~/.claude/.credentials.json` or the macOS Keychain.
- Never send a plan OAuth token to a vendor API endpoint from ReviDotNet code.
- Never accept another person's OAuth token — no pasted tokens in `.rcfg` or
  `.tool` files, no per-user token storage, no token brokering.
- Launch the vendor binary and let it resolve its own login.

The operator supplying their *own* `CLAUDE_CODE_OAUTH_TOKEN` through the
environment for their own headless deployment is fine — that is the documented
CI pattern. Holding *other people's* tokens is what turns a control surface into
a third-party developer routing plan credentials.

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

- [Use the Claude Agent SDK with your Claude plan](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan) — June 15 pause notice
- [Using agents according to our Usage Policy](https://support.claude.com/en/articles/12005017-using-agents-according-to-our-usage-policy)
- [Use Claude Code with your Pro or Max plan](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan)
- [Claude Code GitHub Actions](https://code.claude.com/docs/en/github-actions) — subscription OAuth in unattended runs
- [Codex — Non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode) and [Authentication](https://developers.openai.com/codex/auth)
- [*The Register*, April 6 2026](https://www.theregister.com/2026/04/06/anthropic_closes_door_on_subscription/) — Anthropic statement on third-party tools
- [Claude Code — Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- [Claude Agent SDK — Overview](https://code.claude.com/docs/en/agent-sdk/overview)
- [Claude Agent SDK — Quickstart](https://code.claude.com/docs/en/agent-sdk/quickstart)
- [Claude Code — Authentication](https://code.claude.com/docs/en/authentication)
- [Claude Code — Run Claude Code programmatically](https://code.claude.com/docs/en/headless)
- [Agent Client Protocol](https://github.com/agentclientprotocol/agent-client-protocol) (Apache-2.0)
- [acp-csharp](https://github.com/nuskey8/acp-csharp) (MIT, NuGet `AgentClientProtocol`)
- [T3 Code](https://github.com/pingdotgg/t3code)
- [openai/codex discussion #8338](https://github.com/openai/codex/discussions/8338)
