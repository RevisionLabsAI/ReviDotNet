using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Decisions;

/// <summary>Wire fixtures for strict SystemOne validation and operational behavior.</summary>
public sealed class SystemOneTests
{
    private const string Good = """
        {"model":"jev-1.13.0","answers":{
        "queue":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"other":0.2},"confidence":0.7},
        "urgent":{"type":"noul","noul":0.9},
        "severity":{"type":"score","score":1.25,"confidence":0.6,"probabilities":{"0":0.0,"1":0.75,"2":0.25},"legend":{"0":"Low","1":"Medium","2":"High"}}
        },"usage":{"input_tokens":123,"output_tokens":20}}
        """;
    private static DecisionRequest Request() => new("jev-1.13.0", new { text = "Untrusted \"quotes\" {not-a-template}" }, new Dictionary<string, DecisionQuestion>
    {
        ["queue"] = new ChoiceQuestion(new { question = "Which queue?", context = new[] { "example" } }, new Dictionary<string, object?> { ["billing"] = new { description = "Payments" }, ["other"] = null }),
        ["urgent"] = new BooleanQuestion("Urgent?"), ["severity"] = new ScoreQuestion("Severity?", ["Low", "Medium", "High"])
    });
    private static ProviderProfile Provider() => new() { Name = "test", APIURL = "https://example.invalid/", APIKey = "fixture-key", Protocol = Protocol.SystemOne };

    [Fact]
    public async Task CancellationDuringPostRequestSpacingDoesNotLoseReceivedUsage()
    {
        ProviderProfile provider = Provider(); provider.DelayBetweenRequestsMs = 1000;
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) })));
        using SystemOneClient client = new(provider, http);
        DecisionRun run = await client.EvaluateAsync(Request(), new DecisionOptions { Timeout = TimeSpan.FromMilliseconds(300) });
        run.Usage.InputTokens.Should().Be(123);
    }

    [Fact]
    public async Task SerializesStructuredQuestionsAndPreservesAllPrimitives()
    {
        Handler handler = new(async (request, token) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/v1/systemone");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            JsonNode payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            payload["questions"]!["queue"]!["instructions"]!["question"]!.GetValue<string>().Should().Be("Which queue?");
            payload["state"]!["text"]!.GetValue<string>().Should().Contain("{not-a-template}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) };
        });
        using HttpClient http = new(handler);
        using SystemOneClient client = new(Provider(), http);
        DecisionRun run = await client.EvaluateAsync(Request());
        run.Choice("queue").Probabilities.Should().HaveCount(2);
        run.Noul("urgent").Probability.Should().Be(0.9);
        run.Score("severity").Value.Should().Be(1.25);
        run.Score("severity").Legend.Should().HaveCount(3);
        run.Usage.InputTokens.Should().Be(123);
    }

    [Theory]
    [InlineData("missing-answer")]
    [InlineData("unknown-label")]
    [InlineData("invalid-sum")]
    [InlineData("out-of-range")]
    [InlineData("wrong-type")]
    [InlineData("inconsistent-score")]
    public async Task RejectsMalformedAndIncompleteResponses(string mutation)
    {
        JsonNode data = JsonNode.Parse(Good)!;
        switch (mutation)
        {
            case "missing-answer": data["answers"]!.AsObject().Remove("urgent"); break;
            case "unknown-label": data["answers"]!["queue"]!["choice"] = "secret-content"; break;
            case "invalid-sum": data["answers"]!["queue"]!["probabilities"]!["billing"] = 0.5; break;
            case "out-of-range": data["answers"]!["urgent"]!["noul"] = 1.5; break;
            case "wrong-type": data["answers"]!["urgent"]!["type"] = "choice"; break;
            case "inconsistent-score": data["answers"]!["severity"]!["score"] = 0.4; break;
        }
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data.ToJsonString()) })));
        using SystemOneClient client = new(Provider(), http);
        Func<Task> action = () => client.EvaluateAsync(Request());
        (await action.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().NotContain("secret-content");
    }

    [Theory]
    [InlineData(401, 1)]
    [InlineData(422, 1)]
    [InlineData(429, 3)]
    [InlineData(529, 3)]
    public async Task RetriesOnlyDocumentedTransientStatuses(int status, int expectedAttempts)
    {
        Handler handler = new((_, _) =>
        {
            HttpResponseMessage response = new((HttpStatusCode)status) { Content = new StringContent("private echoed state") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using HttpClient http = new(handler);
        using SystemOneClient client = new(Provider(), http);
        Func<Task> action = () => client.EvaluateAsync(Request(), new DecisionOptions { RetryLimit = 2 });
        (await action.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().NotContain("private");
        handler.Attempts.Should().Be(expectedAttempts);
    }

    [Fact]
    public async Task DeadlineAndCancellationReachHttpHandler()
    {
        using HttpClient http = new(new Handler(async (_, token) => { await Task.Delay(10000, token); return new HttpResponseMessage(HttpStatusCode.OK); }));
        using SystemOneClient client = new(Provider(), http);
        Func<Task> action = () => client.EvaluateAsync(Request(), new DecisionOptions { Timeout = TimeSpan.FromMilliseconds(30) });
        await action.Should().ThrowAsync<OperationCanceledException>();
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Func<Task> cancelledAction = () => client.EvaluateAsync(Request(), token: cancelled.Token);
        await cancelledAction.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void DecisionOnlyProviderDoesNotCreateChatOrEmbeddingClients()
    {
        ProviderProfile provider = Provider(); provider.Init();
        provider.DecisionClient.Should().BeOfType<SystemOneClient>();
        provider.InferenceClient.Should().BeNull(); provider.EmbeddingClient.Should().BeNull();
        ((IDisposable)provider.DecisionClient!).Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Attempts++; return send(request, cancellationToken); }
    }
}
