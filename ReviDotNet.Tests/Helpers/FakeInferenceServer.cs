using System.Text.Json;
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
            context.Response.ContentType = "application/json";
            var response = new
            {
                choices = new object[]
                {
                    new { text = "Hello world (prompt)", finish_reason = "stop" }
                }
            };
            await context.Response.WriteAsJsonAsync(response);
        });

        // OpenAI: chat completions
        app.MapPost("/v1/chat/completions", async context =>
        {
            context.Response.ContentType = "application/json";
            var response = new
            {
                choices = new object[]
                {
                    new { message = new { content = "Hello world (chat)" }, finish_reason = "stop" }
                }
            };
            await context.Response.WriteAsJsonAsync(response);
        });

        // Gemini prompt
        app.MapPost("/v1beta/models/{model}:generateContent", async context =>
        {
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
            await context.Response.WriteAsJsonAsync(response);
        });

        // Gemini streaming endpoint (we can just reuse same shape for completion callback not used in these tests)
        app.MapPost("/v1beta/models/{model}:streamGenerateContent", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            // A minimal SSE stream with a single data message
            await context.Response.WriteAsync("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"chunk\"}]},\"finishReason\":\"STOP\"}]}\n\n");
        });

        app.StartAsync().GetAwaiter().GetResult();
        var server = app.GetTestServer();
        var baseAddress = server.BaseAddress ?? new Uri("http://localhost");
        return (server, baseAddress);
    }
}