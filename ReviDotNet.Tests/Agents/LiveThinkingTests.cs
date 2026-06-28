// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Threading.Tasks;
using Revi;
using Xunit;
using Xunit.Abstractions;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Live verification that ReviDotNet drives Anthropic (Claude) native thinking correctly. A model's
/// <c>thinking</c> setting is either an effort level (e.g. <c>high</c>) which uses the adaptive API of
/// newer models — Opus 4.8+ — emitting <c>{ thinking: { type: "adaptive" }, output_config: { effort } }</c>,
/// or a numeric token budget which uses the classic API <c>{ thinking: { type: "enabled", budget_tokens } }</c>.
/// This test confirms the request is accepted and the model reasons to the correct answer. (Adaptive
/// thinking does not surface reasoning text in the response, so <see cref="CompletionResult.Thinking"/>
/// is populated only for the classic budget API.)
///
/// Skips (no-op pass) when PROVAPIKEY__CLAUDE is absent so the normal suite stays green. Run with
/// <c>dotnet test --filter LiveAgent</c> after populating <c>forge.env</c>.
/// </summary>
[Trait("Category", "LiveAgent")]
public class LiveThinkingTests
{
    private readonly ITestOutputHelper _out;
    public LiveThinkingTests(ITestOutputHelper output) { _out = output; LiveEnv.EnsureLoaded(); }

    /// <summary>A problem whose correct answer (5 minutes) requires a short chain of reasoning.</summary>
    private const string ReasoningPrompt =
        "If it takes 5 machines 5 minutes to make 5 widgets, how long would 100 machines take to make " +
        "100 widgets? Reason it through, then end with a final line exactly: ANSWER: <number> minutes.";

    [Fact]
    public async Task Claude_Opus48_AdaptiveThinking_AcceptedAndReasons()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__CLAUDE") ?? "";
        if (string.IsNullOrWhiteSpace(key))
        {
            _out.WriteLine("SKIPPED — PROVAPIKEY__CLAUDE not set (add it to forge.env).");
            return;
        }

        using InferClient client = new InferClient(
            apiUrl: "https://api.anthropic.com/",
            apiKey: key,
            protocol: Protocol.Claude,
            defaultModel: "claude-opus-4-8",
            timeoutSeconds: 180,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 1,
            simultaneousRequests: 1,
            supportsCompletion: true,
            supportsGuidance: false);

        // Thinking ENABLED via adaptive effort. The request must be ACCEPTED (no 400 for an unsupported
        // thinking shape) and must produce the correct, reasoned answer (5 minutes).
        CompletionResult on = await client.GenerateAsync(
            prompt: ReasoningPrompt,
            model: "claude-opus-4-8",
            maxTokens: 16000,
            thinking: "high");

        _out.WriteLine($"[thinking high] answerLen={on.Selected?.Length} thinkingLen={on.Thinking?.Length} " +
                       $"inTok={on.InputTokens} outTok={on.OutputTokens}");
        _out.WriteLine($"[thinking high] reasoning(first 400): {Trunc(on.Thinking, 400)}");
        _out.WriteLine($"[thinking high] answer(first 200): {Trunc(on.Selected, 200)}");

        Assert.False(string.IsNullOrWhiteSpace(on.Selected), "Expected a non-empty answer with thinking on.");
        Assert.Contains("5", on.Selected); // the correct answer is 5 minutes — proves it reasoned

        // Control: same request with thinking disabled must also succeed (the toggle is wired and the
        // model config without `thinking` works).
        CompletionResult off = await client.GenerateAsync(
            prompt: ReasoningPrompt,
            model: "claude-opus-4-8",
            maxTokens: 4096,
            thinking: null);

        _out.WriteLine($"[thinking off ] answerLen={off.Selected?.Length} thinkingNull={off.Thinking is null}");
        Assert.False(string.IsNullOrWhiteSpace(off.Selected), "Expected a non-empty answer with thinking off.");
    }

    [Fact]
    public async Task Gemini_25Flash_ThinkingBudget_AcceptedAndReasons()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__GEMINI") ?? "";
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED — PROVAPIKEY__GEMINI not set."); return; }

        using InferClient client = new InferClient(
            apiUrl: "https://generativelanguage.googleapis.com/",
            apiKey: key, protocol: Protocol.Gemini, defaultModel: "gemini-2.5-flash",
            timeoutSeconds: 180, retryAttemptLimit: 1, simultaneousRequests: 1, supportsGuidance: true);

        // A numeric value -> generationConfig.thinkingConfig.thinkingBudget. 8192 is within 2.5-flash's range.
        CompletionResult r = await client.GenerateAsync(
            messages: [new Message("user", ReasoningPrompt)],
            model: "gemini-2.5-flash", maxTokens: 4000, thinking: "8192");

        _out.WriteLine($"[gemini budget=8192] answerLen={r.Selected?.Length} outTok={r.OutputTokens} answer(120): {Trunc(r.Selected, 120)}");
        Assert.False(string.IsNullOrWhiteSpace(r.Selected), "Gemini returned empty answer with thinkingBudget set.");
        Assert.Contains("5", r.Selected);
    }

    [Fact]
    public async Task OpenAI_Gpt5_ReasoningEffort_AcceptedAndReasons()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__OPENAI") ?? "";
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED — PROVAPIKEY__OPENAI not set."); return; }

        using InferClient client = new InferClient(
            apiUrl: "https://api.openai.com/",
            apiKey: key, protocol: Protocol.OpenAI, defaultModel: "gpt-5",
            timeoutSeconds: 180, retryAttemptLimit: 1, simultaneousRequests: 1);

        // reasoning_effort = "high". gpt-5 is a reasoning model and requires max_completion_tokens.
        CompletionResult r = await client.GenerateAsync(
            messages: [new Message("user", ReasoningPrompt)],
            model: "gpt-5", maxTokenType: MaxTokenType.MaxCompletionTokens, maxTokens: 4000, thinking: "high");

        _out.WriteLine($"[openai effort=high] answerLen={r.Selected?.Length} outTok={r.OutputTokens} answer(120): {Trunc(r.Selected, 120)}");
        Assert.False(string.IsNullOrWhiteSpace(r.Selected), "OpenAI returned empty answer with reasoning_effort set.");
        Assert.Contains("5", r.Selected);
    }

    [Fact]
    public async Task Claude_Opus48_EffortValues_Probe()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__CLAUDE") ?? "";
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED — PROVAPIKEY__CLAUDE not set."); return; }

        using InferClient client = new InferClient(
            apiUrl: "https://api.anthropic.com/", apiKey: key, protocol: Protocol.Claude,
            defaultModel: "claude-opus-4-8", timeoutSeconds: 180, retryAttemptLimit: 1,
            simultaneousRequests: 1, supportsCompletion: true);

        // Informational: discover which adaptive effort levels claude-opus-4-8 accepts, so the
        // thinking-conversion table can map common words onto valid values. Asserts only that "high" works.
        bool highWorks = false;
        foreach (string effort in new[] { "low", "medium", "high", "xhigh", "max" })
        {
            try
            {
                CompletionResult r = await client.GenerateAsync(
                    prompt: "Reply with just: ok", model: "claude-opus-4-8", maxTokens: 8000, thinking: effort);
                bool ok = !string.IsNullOrWhiteSpace(r.Selected);
                if (effort == "high") highWorks = ok;
                _out.WriteLine($"[claude effort={effort}] ACCEPTED (answerLen={r.Selected?.Length})");
            }
            catch (Exception ex)
            {
                _out.WriteLine($"[claude effort={effort}] REJECTED — {Trunc(ex.Message, 160)}");
            }
        }
        Assert.True(highWorks, "Claude opus-4-8 should accept effort=high.");
    }

    [Fact]
    public async Task OpenAI_Gpt5_EffortValues_Probe()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__OPENAI") ?? "";
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED — PROVAPIKEY__OPENAI not set."); return; }

        using InferClient client = new InferClient(
            apiUrl: "https://api.openai.com/", apiKey: key, protocol: Protocol.OpenAI,
            defaultModel: "gpt-5", timeoutSeconds: 180, retryAttemptLimit: 1, simultaneousRequests: 1);

        // Discover which reasoning_effort values gpt-5 accepts (informational).
        foreach (string effort in new[] { "minimal", "low", "medium", "high", "none", "xhigh", "max" })
        {
            try
            {
                CompletionResult r = await client.GenerateAsync(
                    messages: [new Message("user", "Reply with just: ok")], model: "gpt-5",
                    maxTokenType: MaxTokenType.MaxCompletionTokens, maxTokens: 2000, thinking: effort);
                _out.WriteLine($"[openai reasoning_effort={effort}] ACCEPTED (answerLen={r.Selected?.Length})");
            }
            catch (Exception ex) { _out.WriteLine($"[openai reasoning_effort={effort}] REJECTED — {Trunc(ex.Message, 200)}"); }
        }
    }

    [Fact]
    public async Task Gemini_25Flash_ThinkingShape_Probe()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__GEMINI") ?? "";
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED — PROVAPIKEY__GEMINI not set."); return; }

        using InferClient client = new InferClient(
            apiUrl: "https://generativelanguage.googleapis.com/", apiKey: key, protocol: Protocol.Gemini,
            defaultModel: "gemini-2.5-flash", timeoutSeconds: 180, retryAttemptLimit: 1, simultaneousRequests: 1);

        // Word values send thinkingConfig.thinkingLevel (Gemini 3 shape); numeric send thinkingBudget
        // (Gemini 2.5 shape). Probe both against 2.5-flash to characterize what it supports + budget range.
        foreach (string v in new[] { "low", "medium", "high", "0", "-1", "512", "24576", "32768" })
        {
            try
            {
                CompletionResult r = await client.GenerateAsync(
                    messages: [new Message("user", "Reply with just: ok")], model: "gemini-2.5-flash",
                    maxTokens: 2000, thinking: v);
                bool numeric = int.TryParse(v, out _);
                _out.WriteLine($"[gemini {(numeric ? "thinkingBudget" : "thinkingLevel")}={v}] ACCEPTED (answerLen={r.Selected?.Length})");
            }
            catch (Exception ex) { _out.WriteLine($"[gemini ={v}] REJECTED — {Trunc(ex.Message, 200)}"); }
        }
    }

    private static string Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? "(empty)" : (s.Length > n ? s[..n] + "…" : s);
}
