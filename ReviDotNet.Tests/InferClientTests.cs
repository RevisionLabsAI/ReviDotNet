using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace ReviDotNet.Tests;

public class InferClientTests
{
    private static (InferClient Client, TestServer Server) CreateClient(Protocol protocol, bool supportsCompletion, string defaultModel = "test-model", string apiKey = "test-key")
    {
        var (server, baseAddress) = FakeInferenceServer.Create();
        var httpClient = server.CreateClient();
        httpClient.BaseAddress = baseAddress; // set to TestServer base

        var client = new InferClient(
            apiUrl: baseAddress.ToString(),
            apiKey: apiKey,
            protocol: protocol,
            defaultModel: defaultModel,
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            supportsCompletion: supportsCompletion,
            supportsGuidance: false,
            defaultGuidanceType: GuidanceType.Disabled,
            defaultGuidanceString: null,
            httpClientOverride: httpClient
        );

        return (client, server);
    }

    [Fact]
    public async Task GenerateAsync_Prompt_vLLM_ReturnsParsedText()
    {
        var (client, server) = CreateClient(Protocol.vLLM, supportsCompletion: true);
        using var _ = client; using var __ = server;

        var response = await client.GenerateAsync("Say hello", model: "test-model");

        response.Selected.Should().Be("Hello world (prompt)");
        response.FinishReason.Should().Be("stop");
        response.FullPrompt.Should().Be("Say hello");
        response.Outputs.Should().ContainSingle().Which.Should().Be("Hello world (prompt)");
    }

    [Fact]
    public async Task GenerateAsync_Chat_OpenAI_ReturnsParsedMessageContent()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, supportsCompletion: false);
        using var _ = client; using var __ = server;

        var messages = new List<Message>
        {
            new("system", "You are helpful"),
            new("user", "Say hello")
        };

        var response = await client.GenerateAsync(messages, model: "gpt-like");

        response.Selected.Should().Be("Hello world (chat)");
        response.FinishReason.Should().Be("stop");
        response.FullPrompt.Should().Contain("Say hello"); // serialized messages
    }

    [Fact]
    public async Task GenerateAsync_Prompt_Gemini_UsesGeminiEndpointAndParses()
    {
        var (client, server) = CreateClient(Protocol.Gemini, supportsCompletion: true, defaultModel: "gemini-pro", apiKey: "abc123");
        using var _ = client; using var __ = server;

        var response = await client.GenerateAsync("Hi Gemini", model: "gemini-pro");

        response.Selected.Should().Be("Hello world (gemini)");
        response.FinishReason.Should().Be("STOP");
        response.FullPrompt.Should().Be("Hi Gemini");
    }

    [Fact]
    public async Task GenerateAsync_Prompt_WhenCompletionNotSupported_Throws()
    {
        var (client, server) = CreateClient(Protocol.OpenAI, supportsCompletion: false);
        using var _ = client; using var __ = server;

        Func<Task> act = async () => await client.GenerateAsync("Should fail");
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*does not support*");
    }
}