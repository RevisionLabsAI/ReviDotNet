// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Revi.Tests.Helpers;

/// <summary>
/// One scripted turn in a <see cref="FakeInferenceServer.CreateWithScript"/> response sequence.
/// Each turn is the next AgentStepResponse the fake server will return on a chat completion.
/// </summary>
public sealed record FakeAgentTurn(
    string? Signal,
    IReadOnlyList<(string Name, string Input)> ToolCalls,
    string Content,
    string? Thinking = null,
    int? PromptTokens = null,
    int? CompletionTokens = null)
{
    public static FakeAgentTurn Step(string signal, string content, params (string Name, string Input)[] toolCalls)
        => new(signal, toolCalls.ToArray(), content);
}

public static class FakeInferenceServer
{
    public static (TestServer Server, Uri BaseAddress) Create()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        // vLLM/OpenAI: prompt completions
        app.MapPost("/v1/completions", async context =>
        {
            string body = await new System.IO.StreamReader(context.Request.Body).ReadToEndAsync();
            if (body.Contains("__HANG_HEADERS__"))
            {
                // Do not send headers/body
                await Task.Delay(TimeSpan.FromMinutes(5), context.RequestAborted);
                return;
            }
            if (body.Contains("__SLOW_BODY__"))
            {
                context.Response.ContentType = "application/json";
                await context.Response.StartAsync(); // send headers now
                await Task.Delay(1500, context.RequestAborted); // delay body
                var slowResponse = new { choices = new object[] { new { text = "Hello world (prompt)", finish_reason = "stop" } } };
                var slowJson = JsonSerializer.Serialize(slowResponse);
                await context.Response.WriteAsync(slowJson);
                return;
            }
            context.Response.ContentType = "application/json";
            var response = new
            {
                choices = new object[]
                {
                    new { text = "Hello world (prompt)", finish_reason = "stop" }
                }
            };
            var json = JsonSerializer.Serialize(response);
            await context.Response.WriteAsync(json);
        });

        // OpenAI: chat completions
        app.MapPost("/v1/chat/completions", async context =>
        {
            string body = await new System.IO.StreamReader(context.Request.Body).ReadToEndAsync();
            if (body.Contains("__HANG_HEADERS__"))
            {
                await Task.Delay(TimeSpan.FromMinutes(5), context.RequestAborted);
                return;
            }
            if (body.Contains("__SLOW_BODY__"))
            {
                context.Response.ContentType = "application/json";
                await context.Response.StartAsync();
                await Task.Delay(1500, context.RequestAborted);
                var slowResponse = new { choices = new object[] { new { message = new { content = "Hello world (chat)" }, finish_reason = "stop" } } };
                var slowJson = JsonSerializer.Serialize(slowResponse);
                await context.Response.WriteAsync(slowJson);
                return;
            }
            context.Response.ContentType = "application/json";
            var response = new
            {
                choices = new object[]
                {
                    new { message = new { content = "Hello world (chat)" }, finish_reason = "stop" }
                }
            };
            var json = JsonSerializer.Serialize(response);
            await context.Response.WriteAsync(json);
        });

        // Gemini prompt
        app.MapPost("/v1beta/models/{model}:generateContent", async context =>
        {
            string body = await new System.IO.StreamReader(context.Request.Body).ReadToEndAsync();
            if (body.Contains("__HANG_HEADERS__"))
            {
                await Task.Delay(TimeSpan.FromMinutes(5), context.RequestAborted);
                return;
            }
            if (body.Contains("__SLOW_BODY__"))
            {
                context.Response.ContentType = "application/json";
                await context.Response.StartAsync();
                await Task.Delay(1500, context.RequestAborted);
                var slowResp = new { candidates = new object[] { new { content = new { parts = new object[] { new { text = "Hello world (gemini)" } } }, finishReason = "STOP" } } };
                var slowJson = JsonSerializer.Serialize(slowResp);
                await context.Response.WriteAsync(slowJson);
                return;
            }
            context.Response.ContentType = "application/json";
            var response = new
            {
                candidates = new object[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new object[]
                            {
                                new { text = "Hello world (gemini)" }
                            }
                        },
                        finishReason = "STOP"
                    }
                }
            };
            var json = JsonSerializer.Serialize(response);
            await context.Response.WriteAsync(json);
        });

        // Gemini streaming endpoint
        app.MapPost("/v1beta/models/{model}:streamGenerateContent", async context =>
        {
            string body = await new System.IO.StreamReader(context.Request.Body).ReadToEndAsync();
            if (body.Contains("__STREAM_HANG_HEADERS__"))
            {
                // Don't send headers
                await Task.Delay(TimeSpan.FromMinutes(5), context.RequestAborted);
                return;
            }
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync();
            await context.Response.WriteAsync("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"chunk1\"}]},\"finishReason\":\"STOP\"}]}\n\n");
            if (body.Contains("__STREAM_IDLE__"))
            {
                await Task.Delay(1500, context.RequestAborted); // idle before next chunk
            }
            await context.Response.WriteAsync("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"chunk2\"}]},\"finishReason\":\"STOP\"}]}\n\n");
        });

        // OpenAI: embeddings
        app.MapPost("/v1/embeddings", async context =>
        {
            context.Response.ContentType = "application/json";
            var response = new
            {
                @object = "list",
                data = new object[]
                {
                    new
                    {
                        @object = "embedding",
                        index = 0,
                        embedding = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }
                    }
                },
                model = "text-embedding-ada-002",
                usage = new
                {
                    prompt_tokens = 5,
                    total_tokens = 5
                }
            };
            var json = JsonSerializer.Serialize(response);
            await context.Response.WriteAsync(json);
        });

        // Gemini: embeddings
        app.MapPost("/v1beta/models/{model}:embedContent", async context =>
        {
            context.Response.ContentType = "application/json";
            var response = new
            {
                embedding = new
                {
                    values = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }
                }
            };
            var json = JsonSerializer.Serialize(response);
            await context.Response.WriteAsync(json);
        });

        app.StartAsync().GetAwaiter().GetResult();
        var server = app.GetTestServer();
        var baseAddress = server.BaseAddress ?? new Uri("http://localhost");
        return (server, baseAddress);
    }

    /// <summary>
    /// Creates a fake inference server that returns a deterministic sequence of agent step
    /// responses. Each call to <c>/v1/chat/completions</c> consumes the next turn and emits
    /// it as a JSON-serialised <see cref="AgentStepResponse"/> wrapped in the OpenAI chat
    /// envelope (with usage tokens populated so cost tracking can be exercised in tests).
    /// Once the script is exhausted, further calls echo the final turn (so callers can
    /// safely over-allocate without surprises).
    /// </summary>
    public static (TestServer Server, Uri BaseAddress) CreateWithScript(IReadOnlyList<FakeAgentTurn> turns)
    {
        if (turns == null || turns.Count == 0)
            throw new ArgumentException("WithScript requires at least one turn.", nameof(turns));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        int counter = 0;

        app.MapPost("/v1/chat/completions", async context =>
        {
            int idx = Interlocked.Increment(ref counter) - 1;
            FakeAgentTurn turn = turns[Math.Min(idx, turns.Count - 1)];

            var stepResponse = new
            {
                signal = turn.Signal,
                tool_calls = turn.ToolCalls.Select(tc => new { name = tc.Name, input = tc.Input }).ToArray(),
                content = turn.Content,
                thinking = turn.Thinking
            };
            string stepJson = JsonSerializer.Serialize(
                stepResponse,
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            var response = new
            {
                choices = new object[]
                {
                    new { message = new { content = stepJson }, finish_reason = "stop" }
                },
                usage = new
                {
                    prompt_tokens = turn.PromptTokens ?? 100,
                    completion_tokens = turn.CompletionTokens ?? 50,
                    total_tokens = (turn.PromptTokens ?? 100) + (turn.CompletionTokens ?? 50)
                }
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        });

        // Embedding endpoint — kept for completeness so a single TestServer can host both,
        // matching the surface area of Create().
        app.MapPost("/v1/embeddings", async context =>
        {
            context.Response.ContentType = "application/json";
            var response = new
            {
                @object = "list",
                data = new object[] { new { @object = "embedding", index = 0, embedding = new[] { 0.1f } } },
                model = "text-embedding-ada-002",
                usage = new { prompt_tokens = 1, total_tokens = 1 }
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        });

        app.StartAsync().GetAwaiter().GetResult();
        var server = app.GetTestServer();
        var baseAddress = server.BaseAddress ?? new Uri("http://localhost");
        return (server, baseAddress);
    }
}

/// <summary>
/// Lightweight <see cref="IBuiltInTool"/> for tests. Returns a fixed string output (or, when
/// a func is supplied, the func's result) without making any external calls. Register via
/// <c>ToolManager.Register</c> before running an agent and <c>Unregister</c> afterwards.
/// </summary>
public sealed class FakeBuiltInTool : IBuiltInTool
{
    private readonly Func<string, string> _handler;

    public FakeBuiltInTool(string name, string fixedOutput, string description = "Test tool")
    {
        Name = name;
        Description = description;
        _handler = _ => fixedOutput;
    }

    public FakeBuiltInTool(string name, Func<string, string> handler, string description = "Test tool")
    {
        Name = name;
        Description = description;
        _handler = handler;
    }

    public string Name { get; }
    public string Description { get; }

    public Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        try
        {
            return Task.FromResult(new ToolCallResult { ToolName = Name, Output = _handler(input) });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolCallResult { ToolName = Name, Failed = true, ErrorMessage = ex.Message });
        }
    }
}