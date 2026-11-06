// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Revi.ToolUse;
using Xunit;

namespace ReviDotNet.Tests.ToolUse;

/// <summary>
/// The native tool-use layer: request shaping for the Anthropic Messages API, the streaming parser,
/// and the session loop with its confirmation gate. Every test runs against a scripted HTTP handler;
/// nothing reaches the network.
/// </summary>
public class ToolChatTests
{
    private static readonly ToolDefinition Weather = new(
        "get_weather", "Current weather for a city.",
        JObject.Parse("{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}}}"), strict: true);

    private static readonly ToolDefinition SendMail = new(
        "send_mail", "Sends an email.",
        JObject.Parse("{\"type\":\"object\",\"properties\":{\"to\":{\"type\":\"string\"},\"body\":{\"type\":\"string\"}}}"));

    // ── Request shaping ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildBody_ShapesToolsSystemThinkingAndCacheBreakpoints()
    {
        var request = new ToolChatRequest
        {
            Model = "claude-sonnet-5-5",
            System = "You are brief.",
            Messages = new[] { ChatMessage.User("hi") },
            Tools = new[] { Weather, ToolDefinition.ClaudeWebSearch(3) },
            Thinking = ThinkingMode.Adaptive,
            Effort = "Low",
            ShowThinking = true,
            MaxTokens = 2048,
            UseRefusalFallbacks = true
        };

        JObject body = ClaudePayloads.BuildBody(request);

        body["model"]!.Value<string>().Should().Be("claude-sonnet-5-5");
        body["max_tokens"]!.Value<int>().Should().Be(2048);
        body["system"]![0]!["text"]!.Value<string>().Should().Be("You are brief.");
        body["system"]![0]!["cache_control"]!["type"]!.Value<string>().Should().Be("ephemeral");
        var tools = (JArray)body["tools"]!;
        tools.Count.Should().Be(2);
        tools[0]["name"]!.Value<string>().Should().Be("get_weather");
        tools[0]["strict"]!.Value<bool>().Should().BeTrue();
        tools[0]["input_schema"]!["additionalProperties"]!.Value<bool>().Should().BeFalse();
        tools[0]["input_schema"]!["required"]!.Values<string>().Should().Equal("city");
        tools[0]["cache_control"].Should().BeNull("only the last tool carries the breakpoint");
        tools[1]["type"]!.Value<string>().Should().Be("web_search_20260209");
        tools[1]["max_uses"]!.Value<int>().Should().Be(3);
        tools[1]["cache_control"]!["type"]!.Value<string>().Should().Be("ephemeral");
        body["thinking"]!["type"]!.Value<string>().Should().Be("adaptive");
        body["thinking"]!["display"]!.Value<string>().Should().Be("summarized");
        body["output_config"]!["effort"]!.Value<string>().Should().Be("low");
        body["fallbacks"]!.Value<string>().Should().Be("default");
        body["temperature"].Should().BeNull("sampling parameters are rejected on current models");
        ClaudePayloads.Betas(request).Should().Contain(ClaudePayloads.RefusalFallbackBeta);
    }

    [Fact]
    public void BuildBody_BetweenToolsThinkingTakesNoOtherField()
    {
        JObject body = ClaudePayloads.BuildBody(new ToolChatRequest
        {
            Model = "m", Messages = new[] { ChatMessage.User("x") }, Thinking = ThinkingMode.BetweenTools, ShowThinking = true
        });
        JToken.DeepEquals(body["thinking"], new JObject { ["type"] = "between_tools" }).Should().BeTrue();
    }

    [Fact]
    public void Messages_RoundTripThroughJson_IncludingToolUseAndResults()
    {
        var assistant = ChatMessage.Assistant(new[]
        {
            new JObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig" },
            new JObject { ["type"] = "text", ["text"] = "Checking." },
            new JObject { ["type"] = "tool_use", ["id"] = "tu_1", ["name"] = "get_weather", ["input"] = new JObject { ["city"] = "Oslo" } }
        });
        var results = ChatMessage.ToolResults(new[] { new ToolResult("tu_1", "{\"temp\":3}") });

        ChatMessage back = ChatMessage.FromJson(assistant.ToJson());
        back.Role.Should().Be("assistant");
        back.Blocks.Should().HaveCount(3);
        back.ToolUses.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = "tu_1", Name = "get_weather" });
        back.ToolUses[0].Input["city"]!.Value<string>().Should().Be("Oslo");
        back.Text.Should().Be("Checking.");
        ChatMessage.FromJson(results.ToJson()).ToolResultIds.Should().Equal("tu_1");
    }

    // ── Streaming parser ─────────────────────────────────────────────────────────

    [Fact]
    public void StreamParser_RebuildsTextThinkingAndToolInput()
    {
        var parser = new ClaudeStreamParser();
        var events = new List<ToolChatEvent>();
        void Feed(string json) => events.AddRange(parser.Feed(null, json));

        Feed("{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-sonnet-5-5\",\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":200,\"cache_creation_input_tokens\":0,\"output_tokens\":1}}}");
        Feed("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}");
        Feed("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hm\"}}");
        Feed("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"abc\"}}");
        Feed("{\"type\":\"content_block_stop\",\"index\":0}");
        Feed("{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
        Feed("{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Let me \"}}");
        Feed("{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"check.\"}}");
        Feed("{\"type\":\"content_block_stop\",\"index\":1}");
        Feed("{\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_9\",\"name\":\"get_weather\",\"input\":{}}}");
        Feed("{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"ci\"}}");
        Feed("{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"ty\\\": \\\"Oslo\\\"}\"}}");
        Feed("{\"type\":\"content_block_stop\",\"index\":2}");
        Feed("{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":42}}");
        Feed("{\"type\":\"message_stop\"}");

        events.OfType<ThinkingDeltaEvent>().Select(e => e.Text).Should().Equal("hm");
        string.Concat(events.OfType<TextDeltaEvent>().Select(e => e.Text)).Should().Be("Let me check.");
        events.OfType<ToolUseStartedEvent>().Should().ContainSingle().Which.Name.Should().Be("get_weather");
        events.OfType<ToolUseCompletedEvent>().Should().ContainSingle().Which.Request.Input["city"]!.Value<string>().Should().Be("Oslo");

        ToolChatResponse response = parser.Completed!;
        response.StopReason.Should().Be("tool_use");
        response.Usage.Should().Be(new ChatUsage(10, 200, 0, 42));
        response.Model.Should().Be("claude-sonnet-5-5");
        response.Message.Blocks[0]["signature"]!.Value<string>().Should().Be("abc", "thinking blocks replay with their signature");
        response.Message.Blocks[2]["input"]!["city"]!.Value<string>().Should().Be("Oslo");
        response.NeedsToolResults.Should().BeTrue();
    }

    [Fact]
    public void ReadResponse_ToleratesNullStopDetails()
    {
        var body = JObject.Parse("{\"id\":\"m\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"stop_reason\":\"end_turn\",\"stop_details\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
        ToolChatResponse response = ClaudePayloads.ReadResponse(body);
        response.RefusalCategory.Should().BeNull();
        response.Text.Should().Be("hi");

        var parser = new ClaudeStreamParser();
        parser.Feed(null, "{\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":{\"input_tokens\":1}}}");
        parser.Feed(null, "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_details\":null},\"usage\":{\"output_tokens\":2}}");
        parser.Feed(null, "{\"type\":\"message_stop\"}");
        parser.Completed!.RefusalCategory.Should().BeNull();
    }

    [Fact]
    public async Task Client_StreamsSseAndSendsHeaders()
    {
        var handler = new ScriptedHandler(_ => Sse(ToolUseStream("tu_1", "get_weather", "{\"city\":\"Oslo\"}")));
        using var client = NewClient(handler);

        var events = new List<ToolChatEvent>();
        await foreach (ToolChatEvent e in client.StreamAsync(new ToolChatRequest { Model = "m", Messages = new[] { ChatMessage.User("weather?") }, Tools = new[] { Weather }, Betas = new[] { "x-beta" } }))
            events.Add(e);

        events.Last().Should().BeOfType<MessageCompletedEvent>();
        HttpRequestMessage sent = handler.Requests.Single();
        sent.RequestUri!.AbsolutePath.Should().Be("/v1/messages");
        sent.Headers.GetValues("x-api-key").Single().Should().Be("k");
        sent.Headers.GetValues("anthropic-version").Single().Should().Be(ClaudePayloads.ApiVersion);
        sent.Headers.GetValues("anthropic-beta").Single().Should().Be("x-beta");
        sent.Headers.Accept.Single().MediaType.Should().Be("text/event-stream");
        handler.Bodies.Single()["stream"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Client_RetriesRetryableStatusThenSucceeds()
    {
        int calls = 0;
        var handler = new ScriptedHandler(_ => ++calls == 1
            ? new HttpResponseMessage((HttpStatusCode)529) { Content = new StringContent("{\"error\":{\"type\":\"overloaded_error\"}}") }
            : Json(EndTurnJson("ok")));
        using var client = NewClient(handler, new ToolChatClientOptions { RetryInitialDelay = TimeSpan.FromMilliseconds(1), DumpLogs = false });

        ToolChatResponse response = await client.SendAsync(new ToolChatRequest { Model = "m", Messages = new[] { ChatMessage.User("x") } });

        response.Text.Should().Be("ok");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Client_DoesNotRetryUnauthorized()
    {
        int calls = 0;
        var handler = new ScriptedHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"type\":\"authentication_error\",\"message\":\"bad key\"}}") }; });
        using var client = NewClient(handler, new ToolChatClientOptions { RetryInitialDelay = TimeSpan.FromMilliseconds(1), DumpLogs = false });

        Func<Task> act = () => client.SendAsync(new ToolChatRequest { Model = "m", Messages = new[] { ChatMessage.User("x") } });

        await act.Should().ThrowAsync<Revi.InferenceProviderException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Client_BearerAuthAddsOAuthBeta()
    {
        var handler = new ScriptedHandler(_ => Json(EndTurnJson("ok")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        using var client = new ToolChatClient(ClaudeAuth.BearerToken(_ => Task.FromResult("tok")), http, new ToolChatClientOptions { DumpLogs = false });

        await client.SendAsync(new ToolChatRequest { Model = "m", Messages = new[] { ChatMessage.User("x") } });

        HttpRequestMessage sent = handler.Requests.Single();
        sent.Headers.Authorization!.Parameter.Should().Be("tok");
        sent.Headers.GetValues("anthropic-beta").Single().Should().Contain(ClaudeAuth.OAuthBeta);
        sent.Headers.Contains("x-api-key").Should().BeFalse();
    }

    // ── Session loop ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Session_RunsToolLoop_AndReturnsResultsInOneMessage()
    {
        var handler = new ScriptedHandler(
            _ => Sse(ToolUseStream("tu_1", "get_weather", "{\"city\":\"Oslo\"}", ("tu_2", "get_weather", "{\"city\":\"Rome\"}"))),
            _ => Sse(EndTurnStream("Oslo 3°, Rome 18°.")));
        using var client = NewClient(handler);
        var executor = new FakeExecutor { OnExecute = r => Task.FromResult(new ToolResult(r.Id, r.Input["city"] + ":ok")) };
        var session = new ToolChatSession(client, new ToolChatSessionOptions { Model = "m", System = "sys", Tools = new[] { Weather } }, executor);

        var events = await Drain(session.SendAsync("weather in Oslo and Rome?", "now: 14:00"));

        events.OfType<TurnCompletedEvent>().Single().Text.Should().Be("Oslo 3°, Rome 18°.");
        events.OfType<ToolCallFinishedEvent>().Should().HaveCount(2);
        session.Messages.Select(m => m.Role).Should().Equal("user", "system", "assistant", "user", "assistant");
        session.Messages[3].ToolResultIds.Should().Equal("tu_1", "tu_2");
        // The second request replays the assistant's raw blocks and carries the results.
        JObject second = handler.Bodies[1];
        var messages = (JArray)second["messages"]!;
        messages[1]["role"]!.Value<string>().Should().Be("system");
        messages[2]["content"]![1]!["type"]!.Value<string>().Should().Be("tool_use");
        messages[3]["content"]![0]!["tool_use_id"]!.Value<string>().Should().Be("tu_1");
        messages[3]["content"]![0]!["content"]!.Value<string>().Should().Be("Oslo:ok");
        // One moving cache breakpoint on the newest user message only.
        messages[0]["content"]![0]!["cache_control"].Should().BeNull();
        messages[3]["content"]![1]!["cache_control"]!["type"]!.Value<string>().Should().Be("ephemeral", "the breakpoint sits on the last block of the newest user message");
        session.TotalUsage.OutputTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Session_PausesForConfirmation_ThenRunsApprovedAndDeclinesTheRest()
    {
        var handler = new ScriptedHandler(
            _ => Sse(ToolUseStream("tu_1", "send_mail", "{\"to\":\"a@x\",\"body\":\"hi\"}", ("tu_2", "get_weather", "{\"city\":\"Oslo\"}"), ("tu_3", "send_mail", "{\"to\":\"b@x\",\"body\":\"yo\"}"))),
            _ => Sse(EndTurnStream("Sent to a@x; not to b@x.")));
        using var client = NewClient(handler);
        var executor = new FakeExecutor
        {
            OnGate = r => r.Name == "send_mail" ? ToolGateResult.Confirm("Send mail to " + r.Input["to"]) : ToolGateResult.Allow,
            OnExecute = r => Task.FromResult(new ToolResult(r.Id, r.Name + " done"))
        };
        var session = new ToolChatSession(client, new ToolChatSessionOptions { Model = "m", Tools = new[] { Weather, SendMail } }, executor);

        var first = await Drain(session.SendAsync("mail a and b, and the weather"));

        first.OfType<ConfirmationRequiredEvent>().Single().Pending.Select(p => p.Summary).Should().Equal("Send mail to a@x", "Send mail to b@x");
        first.OfType<TurnCompletedEvent>().Should().BeEmpty();
        first.OfType<ToolCallFinishedEvent>().Should().ContainSingle().Which.Request.Name.Should().Be("get_weather", "allowed calls run before the pause");
        session.IsAwaitingConfirmation.Should().BeTrue();
        handler.Requests.Should().HaveCount(1, "no request goes out while waiting on the user");

        var second = await Drain(session.ResolvePendingAsync(new Dictionary<string, bool> { ["tu_1"] = true }));

        second.OfType<TurnCompletedEvent>().Single().Text.Should().Be("Sent to a@x; not to b@x.");
        session.IsAwaitingConfirmation.Should().BeFalse();
        var results = (JArray)handler.Bodies[1]["messages"]![2]!["content"]!;
        results.Select(r => r["tool_use_id"]!.Value<string>()).Should().Equal("tu_1", "tu_2", "tu_3");
        results[0]["content"]!.Value<string>().Should().Be("send_mail done");
        results[0]["is_error"].Should().BeNull();
        results[2]["is_error"]!.Value<bool>().Should().BeTrue();
        executor.Executed.Should().Equal("tu_2", "tu_1");
    }

    [Fact]
    public async Task Session_NewMessageWhileWaiting_DeclinesPendingAndCarriesTheText()
    {
        var handler = new ScriptedHandler(
            _ => Sse(ToolUseStream("tu_1", "send_mail", "{\"to\":\"a@x\",\"body\":\"hi\"}")),
            _ => Sse(EndTurnStream("Okay, not sending.")));
        using var client = NewClient(handler);
        var executor = new FakeExecutor { OnGate = _ => ToolGateResult.Confirm("Send mail") };
        var session = new ToolChatSession(client, new ToolChatSessionOptions { Model = "m", Tools = new[] { SendMail } }, executor);

        await Drain(session.SendAsync("mail a"));
        var events = await Drain(session.SendAsync("actually never mind"));

        events.OfType<TurnCompletedEvent>().Single().Text.Should().Be("Okay, not sending.");
        executor.Executed.Should().BeEmpty();
        var content = (JArray)handler.Bodies[1]["messages"]![2]!["content"]!;
        content[0]["type"]!.Value<string>().Should().Be("tool_result");
        content[0]["is_error"]!.Value<bool>().Should().BeTrue();
        content[1]["type"]!.Value<string>().Should().Be("text");
        content[1]["text"]!.Value<string>().Should().Be("actually never mind");
    }

    [Fact]
    public async Task Session_DeniedCallsBecomeErrorResults_AndToolExceptionsDoNotEndTheTurn()
    {
        var handler = new ScriptedHandler(
            _ => Sse(ToolUseStream("tu_1", "send_mail", "{}", ("tu_2", "get_weather", "{\"city\":\"Oslo\"}"))),
            _ => Sse(EndTurnStream("done")));
        using var client = NewClient(handler);
        var executor = new FakeExecutor
        {
            OnGate = r => r.Name == "send_mail" ? ToolGateResult.Deny("sending is off") : ToolGateResult.Allow,
            OnExecute = _ => throw new InvalidOperationException("boom")
        };
        var session = new ToolChatSession(client, new ToolChatSessionOptions { Model = "m", Tools = new[] { Weather, SendMail } }, executor);

        var events = await Drain(session.SendAsync("go"));

        events.OfType<TurnCompletedEvent>().Should().ContainSingle();
        var results = (JArray)handler.Bodies[1]["messages"]![2]!["content"]!;
        results[0]["content"]!.Value<string>().Should().Be("Not allowed: sending is off");
        results[1]["content"]!.Value<string>().Should().StartWith("The tool failed: boom");
        results.All(r => r["is_error"]!.Value<bool>()).Should().BeTrue();
    }

    [Fact]
    public async Task Session_StopsAtRoundLimit()
    {
        var handler = new ScriptedHandler(_ => Sse(ToolUseStream("tu", "get_weather", "{\"city\":\"Oslo\"}")));
        using var client = NewClient(handler);
        var session = new ToolChatSession(client, new ToolChatSessionOptions { Model = "m", Tools = new[] { Weather }, MaxRoundsPerTurn = 3 }, new FakeExecutor());

        var events = await Drain(session.SendAsync("loop"));

        events.OfType<TurnCompletedEvent>().Single().StopReason.Should().Be("round_limit");
        handler.Requests.Should().HaveCount(3);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ToolChatClient NewClient(ScriptedHandler handler, ToolChatClientOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        return new ToolChatClient(ClaudeAuth.ApiKey("k"), http, options ?? new ToolChatClientOptions { DumpLogs = false });
    }

    private static async Task<List<ToolChatEvent>> Drain(IAsyncEnumerable<ToolChatEvent> events)
    {
        var list = new List<ToolChatEvent>();
        await foreach (ToolChatEvent e in events) list.Add(e);
        return list;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string events) => new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };

    private static string EndTurnJson(string text)
        => "{\"id\":\"msg\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":7}}";

    private static string EndTurnStream(string text)
    {
        var sb = new StringBuilder();
        Event(sb, "message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"model\":\"m\",\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}");
        Event(sb, "content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
        Event(sb, "content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JValue.FromObject(text).ToString(Newtonsoft.Json.Formatting.None) + "}}");
        Event(sb, "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}");
        Event(sb, "message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}");
        Event(sb, "message_stop", "{\"type\":\"message_stop\"}");
        return sb.ToString();
    }

    private static string ToolUseStream(string id, string name, string inputJson, params (string Id, string Name, string InputJson)[] more)
    {
        var sb = new StringBuilder();
        Event(sb, "message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"model\":\"m\",\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}");
        Event(sb, "content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
        Event(sb, "content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"On it.\"}}");
        Event(sb, "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}");
        var calls = new List<(string, string, string)> { (id, name, inputJson) };
        calls.AddRange(more);
        int index = 1;
        foreach (var (cid, cname, cinput) in calls)
        {
            Event(sb, "content_block_start", "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + cid + "\",\"name\":\"" + cname + "\",\"input\":{}}}");
            Event(sb, "content_block_delta", "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JValue.FromObject(cinput).ToString(Newtonsoft.Json.Formatting.None) + "}}");
            Event(sb, "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":" + index + "}");
            index++;
        }
        Event(sb, "message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":9}}");
        Event(sb, "message_stop", "{\"type\":\"message_stop\"}");
        return sb.ToString();
    }

    private static void Event(StringBuilder sb, string name, string data)
        => sb.Append("event: ").Append(name).Append('\n').Append("data: ").Append(data).Append("\n\n");

    private sealed class FakeExecutor : IToolExecutor
    {
        public Func<ToolUseRequest, ToolGateResult> OnGate { get; set; } = _ => ToolGateResult.Allow;
        public Func<ToolUseRequest, Task<ToolResult>> OnExecute { get; set; } = r => Task.FromResult(new ToolResult(r.Id, "ok"));
        public List<string> Executed { get; } = new();

        public ToolGateResult Gate(ToolUseRequest request) => OnGate(request);

        public Task<ToolResult> ExecuteAsync(ToolUseRequest request, CancellationToken cancellationToken)
        {
            lock (Executed) Executed.Add(request.Id);
            return OnExecute(request);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _script;
        private int _next;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) => _script = script;

        public List<HttpRequestMessage> Requests { get; } = new();
        public List<JObject> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            var step = _script[Math.Min(_next, _script.Length - 1)];
            _next++;
            return step(request);
        }
    }
}
