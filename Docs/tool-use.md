# Native tool-use chat (`Revi.ToolUse`)

A second harness beside `AgentRunner`, for conversations rather than batch agents: the model
calls tools natively (provider `tools`), the answer streams, and a call can be paused for the
user's confirmation. Claude protocol only today.

```csharp
using Revi.ToolUse;

var client = new ToolChatClient(ClaudeAuth.ApiKey(key));          // or provider.InferenceClient.CreateToolChat()
var weather = new ToolDefinition("get_weather", "Current weather for a city.",
    JObject.Parse("{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}}}"), strict: true);

var session = new ToolChatSession(client,
    new ToolChatSessionOptions { Model = "claude-sonnet-5-5", System = "Be brief.", Tools = [weather], Effort = "low" },
    new MyExecutor());

await foreach (ToolChatEvent e in session.SendAsync("weather in Oslo?", systemNote: "now: 14:00"))
    switch (e)
    {
        case TextDeltaEvent t: Console.Write(t.Text); break;
        case ConfirmationRequiredEvent c: /* ask the user, then session.ResolvePendingAsync(...) */ break;
        case TurnCompletedEvent done: Console.WriteLine($"\n[{done.StopReason}, {done.Usage.OutputTokens} out]"); break;
    }
```

`IToolExecutor.Gate` returns **Allow**, **Deny(reason)** or **Confirm(summary)** per call. A
confirmation ends the turn with `ConfirmationRequiredEvent`; `ResolvePendingAsync` runs the
approved calls, declines the rest, and continues. A new `SendAsync` while paused declines the
pending calls and carries the new text in the same user message, so the history stays valid.

What the client handles: adaptive or `between_tools` thinking, `output_config.effort`, prompt
cache breakpoints (system, last tool, newest user message), server tools (web search, web fetch,
tool search with `defer_loading`), structured outputs, refusal fallbacks, OAuth bearer tokens,
retries with `Retry-After`, and an SSE parser that rebuilds the assistant message's raw blocks so
thinking signatures and tool calls replay exactly. Histories are append-only; `Restore` answers
any dangling tool calls so a reloaded session is valid.

Tests: `ReviDotNet.Tests/ToolUse/ToolChatTests.cs` (scripted HTTP handler, no network).
