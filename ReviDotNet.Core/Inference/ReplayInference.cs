// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One scripted assistant turn for a deterministic replay run, expressed in terms ReviDotNet.Core can
/// see without referencing the Refinery SDK. The engine layer (which references both Core and the SDK)
/// maps each SDK <c>Revi.Refinery.ReplayTurn</c> onto one of these before calling
/// <see cref="ReplayInference.BuildModel"/>. Mirrors the shape the agent loop expects per LLM call:
/// a transition <see cref="Signal"/>, the step <see cref="Content"/>, optional <see cref="ToolCalls"/>,
/// and usage token counts.
/// </summary>
public sealed record ReplayTurn
{
    /// <summary>Transition signal for this step (e.g. <c>DONE</c>, <c>CONTINUE</c>). Null = no transition.</summary>
    public string? Signal { get; init; }

    /// <summary>Main reasoning / output text for this step. Becomes the agent's final output on the last step.</summary>
    public string? Content { get; init; }

    /// <summary>Names of tools this scripted step requests, in order. Each is emitted with an empty input object.</summary>
    public IReadOnlyList<string>? ToolCalls { get; init; }

    /// <summary>Prompt/input token count this turn reports as usage (for cost-tracking fidelity).</summary>
    public int PromptTokens { get; init; }

    /// <summary>Completion/output token count this turn reports as usage (for cost-tracking fidelity).</summary>
    public int CompletionTokens { get; init; }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that replays a fixed sequence of scripted assistant turns
/// without any network I/O. For each POST it returns the next scripted turn rendered in the exact
/// OpenAI chat-completions wire format <see cref="InferClient"/> parses: the assistant message
/// <c>content</c> is the agent-step JSON (<c>{signal, tool_calls, content, thinking}</c>) the agent
/// loop expects, and <c>usage.prompt_tokens</c>/<c>usage.completion_tokens</c> carry the turn's token
/// counts. Once the script is exhausted, further calls repeat the final turn (so an agent that takes
/// more steps than scripted degrades predictably rather than failing), matching the behaviour of
/// <c>ReviDotNet.Tests.Helpers.FakeInferenceServer.CreateWithScript</c>.
/// </summary>
public sealed class ScriptedInferenceHandler : HttpMessageHandler
{
    private readonly IReadOnlyList<ReplayTurn> _script;
    private int _counter = -1;

    /// <summary>Creates a handler that replays <paramref name="script"/> in order.</summary>
    /// <param name="script">The scripted turns. Must contain at least one turn.</param>
    public ScriptedInferenceHandler(IReadOnlyList<ReplayTurn> script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Count == 0)
            throw new ArgumentException("A replay script requires at least one turn.", nameof(script));
        _script = script;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Consume the request body so anything written to the underlying stream is drained (the agent
        // loop never inspects the scripted handler's view of the request — replay is output-only).
        // Pick the next scripted turn; after exhaustion, repeat the final turn.
        int idx = Interlocked.Increment(ref _counter);
        ReplayTurn turn = _script[Math.Min(idx, _script.Count - 1)];

        // Render the agent-step JSON exactly as the OpenAI/vLLM chat path expects (snake_case
        // tool_calls with name+input; thinking omitted when null).
        var step = new
        {
            signal = turn.Signal,
            tool_calls = (turn.ToolCalls ?? Array.Empty<string>())
                .Select(name => new { name, input = "{}" })
                .ToArray(),
            content = turn.Content ?? string.Empty,
            thinking = (string?)null,
        };
        string stepJson = JsonSerializer.Serialize(
            step,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        // Wrap in the OpenAI chat-completions envelope + usage, the same shape FakeInferenceServer emits.
        var envelope = new
        {
            choices = new object[]
            {
                new { message = new { content = stepJson }, finish_reason = "stop" },
            },
            usage = new
            {
                prompt_tokens = turn.PromptTokens,
                completion_tokens = turn.CompletionTokens,
                total_tokens = turn.PromptTokens + turn.CompletionTokens,
            },
        };
        string body = JsonSerializer.Serialize(envelope);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
        return Task.FromResult(response);
    }
}

/// <summary>
/// Builds a <see cref="ModelProfile"/> backed by a <see cref="ScriptedInferenceHandler"/> so an agent
/// can be run against deterministic, prerecorded outputs with NO live inference. This reuses the
/// Wave-3a per-run <c>modelOverride</c> seam: callers pass the returned model as <c>modelOverride</c>
/// to <c>IAgentService.Run(profile, inputs, …, modelOverride)</c>, and every LLM call the agent makes
/// is served by the script. Mirrors how the test harness overwrites a provider's
/// <see cref="ProviderProfile.InferenceClient"/> with an <see cref="InferClient"/> that has an injected
/// <see cref="HttpClient"/>.
/// </summary>
public static class ReplayInference
{
    /// <summary>
    /// Constructs a scripted <see cref="ModelProfile"/> for replay runs.
    /// </summary>
    /// <param name="modelName">A label for the model (e.g. <c>"__replay/&lt;scenarioId&gt;"</c>). Used as both the
    /// model name and string; it never reaches a real provider.</param>
    /// <param name="script">The scripted assistant turns to replay, in order. Must be non-empty.</param>
    /// <returns>A self-contained <see cref="ModelProfile"/> whose provider's inference client replays the script.</returns>
    public static ModelProfile BuildModel(string modelName, IReadOnlyList<ReplayTurn> script)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(script);
        if (script.Count == 0)
            throw new ArgumentException("A replay script requires at least one turn.", nameof(script));

        // A bare base address — the InferClient ctor rejects URLs that already end in the chat/completions
        // path, and the scripted handler ignores the URL anyway (it answers every POST from the script).
        const string baseUrl = "http://replay.local/";

        // Build a provider via the parameterized ctor (which constructs a real InferClient against the
        // bare host), then overwrite its InferenceClient with one whose HttpClient is the scripted
        // handler — exactly the pattern AgentTestHarness uses with the test server's HttpClient.
        var provider = new ProviderProfile(
            name: "__replay-provider",
            enabled: true,
            protocol: Protocol.OpenAI,
            apiURL: baseUrl,
            apiKey: string.Empty,
            timeoutSeconds: 30,
            retryAttemptLimit: 0,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 4,
            defaultModel: modelName,
            supportsCompletion: false,
            supportsGuidance: true,
            defaultGuidanceType: GuidanceSchemaType.JsonManual);

        provider.InferenceClient = new InferClient(
            apiUrl: baseUrl,
            apiKey: string.Empty,
            protocol: Protocol.OpenAI,
            defaultModel: modelName,
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 0,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 4,
            supportsCompletion: false,
            supportsGuidance: true,
            defaultGuidanceType: GuidanceType.Json,
            defaultGuidanceString: AgentStepSchema.Schema,
            httpClientOverride: new HttpClient(new ScriptedInferenceHandler(script)) { BaseAddress = new Uri(baseUrl) });

        return new ModelProfile
        {
            Name = modelName,
            Enabled = true,
            ModelString = modelName,
            ProviderName = provider.Name!,
            Provider = provider,
            Tier = ModelTier.A,
            ContextWindow = 8192,
            OutputBudget = "512",
            DefaultSystemInputType = InputType.Filled,
            DefaultInstructionInputType = InputType.Filled,
        };
    }
}
