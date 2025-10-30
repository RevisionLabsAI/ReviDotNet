using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Revi.Tests.Helpers;

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

        app.StartAsync().GetAwaiter().GetResult();
        var server = app.GetTestServer();
        var baseAddress = server.BaseAddress ?? new Uri("http://localhost");
        return (server, baseAddress);
    }
}