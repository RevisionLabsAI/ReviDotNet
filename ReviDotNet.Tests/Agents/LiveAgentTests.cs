// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Revi;
using Xunit;
using Xunit.Abstractions;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Live, end-to-end agent runs against the REAL Claude and Gemini APIs — the apparatus for
/// "do agents actually work?". Each test builds the real provider/model exactly as the Forge
/// app's <c>.rcfg</c> files do (same protocol + guidance settings), then runs a tiny echo agent
/// through the real <see cref="AgentRunner"/> and asserts it completes with output.
///
/// Keys are read from environment variables (PROVAPIKEY__CLAUDE / PROVAPIKEY__GEMINI); the
/// project's <c>forge.env</c> is auto-loaded if present (searched upward from the test binary).
/// When a key is absent the test SKIPS (passes as a no-op) so the normal suite stays green —
/// fill in <c>forge.env</c> and run <c>dotnet test --filter LiveAgent</c> to verify live.
/// </summary>
[Trait("Category", "LiveAgent")]
public class LiveAgentTests
{
    private readonly ITestOutputHelper _out;
    public LiveAgentTests(ITestOutputHelper output) { _out = output; LiveEnv.EnsureLoaded(); }

    private const string EchoAgentText = """
        [[information]]
        name = live-echo

        [[loop]]
        entry = echo

        [[state.echo]]
        description = Reply once and finish.

        [[_state.echo.instruction]]
        Reply with the single word "pong". Set "content" to your reply and "signal" to "DONE",
        with an empty "tool_calls" array. Do not call any tools.

        [[state.echo.guardrails]]
        max-steps = 2
        timeout = 60

        [[_loop]]
        echo
          -> [end] [when: DONE]
        """;

    [Fact]
    public async Task Claude_RunsEchoAgent_Completes()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__CLAUDE") ?? "";
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED — PROVAPIKEY__CLAUDE not set (add it to forge.env)."); return; }

        await RunEcho(
            agentName: "live-echo-claude",
            provider: BuildProvider("claude", Protocol.Claude, "https://api.anthropic.com/", key, "claude-sonnet-4-5-20250929", supportsGuidance: false),
            modelName: "claude-sonnet-4-5", modelString: "claude-sonnet-4-5-20250929", providerName: "claude", tokenLimit: 200000);
    }

    [Fact]
    public async Task Gemini_RunsEchoAgent_Completes()
    {
        string key = Environment.GetEnvironmentVariable("PROVAPIKEY__GEMINI") ?? "";
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED — PROVAPIKEY__GEMINI not set (add it to forge.env)."); return; }

        await RunEcho(
            agentName: "live-echo-gemini",
            provider: BuildProvider("gemini", Protocol.Gemini, "https://generativelanguage.googleapis.com/", key, "gemini-2.5-flash", supportsGuidance: true),
            modelName: "gemini-2-5-flash", modelString: "gemini-2.5-flash", providerName: "gemini", tokenLimit: 1048576);
    }

    // ── shared run + builders ────────────────────────────────────────────────

    private async Task RunEcho(string agentName, ProviderProfile provider, string modelName, string modelString, string providerName, int tokenLimit)
    {
        ProviderManager.Add(provider);

        var model = new ModelProfile
        {
            Name = modelName,
            Enabled = true,
            ModelString = modelString,
            ProviderName = providerName,
            Provider = provider,
            Tier = ModelTier.A,
            TokenLimit = tokenLimit,
            MaxTokens = "512",
            DefaultSystemInputType = InputType.Filled,
            DefaultInstructionInputType = InputType.Filled,
        };
        ModelManager.Add(model);

        var agent = AgentBuilder.FromText(EchoAgentText);
        agent.Name = agentName;
        foreach (var state in agent.States)
            if (string.IsNullOrWhiteSpace(state.Model)) state.Model = modelName;
        AgentManager.Add(agent);

        AgentResult result = await Agent.Run(agentName, new Dictionary<string, object> { ["task"] = "Reply with the single word: pong" });

        _out.WriteLine($"[{providerName}] exit={result.ExitReason} steps={result.TotalSteps} output={Trunc(result.FinalOutput, 200)}");

        Assert.True(result.ExitReason == AgentExitReason.Completed,
            $"{providerName} agent did not complete: exit={result.ExitReason}, output='{Trunc(result.FinalOutput, 300)}'");
        Assert.False(string.IsNullOrWhiteSpace(result.FinalOutput), $"{providerName} produced empty output.");
    }

    private static ProviderProfile BuildProvider(string name, Protocol protocol, string apiUrl, string key, string defaultModel, bool supportsGuidance)
    {
        var provider = new ProviderProfile { Name = name, Enabled = true, Protocol = protocol, APIURL = apiUrl, APIKey = key };
        provider.InferenceClient = new InferClient(
            apiUrl: apiUrl,
            apiKey: key,
            protocol: protocol,
            defaultModel: defaultModel,
            timeoutSeconds: 120,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 1,
            simultaneousRequests: 2,
            supportsCompletion: protocol == Protocol.Claude,
            supportsResponseCompletion: true,
            supportsGuidance: supportsGuidance,
            defaultGuidanceType: supportsGuidance ? GuidanceType.Json : GuidanceType.Disabled);
        return provider;
    }

    private static string Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? "(empty)" : (s.Length > n ? s[..n] + "…" : s);
}

/// <summary>Loads forge.env (searched upward from the test binary) into the process environment once.</summary>
internal static class LiveEnv
{
    private static bool _loaded;
    private static readonly object _gate = new();

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            string? path = FindUp("forge.env");
            if (path is null) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                string k = trimmed[..eq].Trim();
                string v = trimmed[(eq + 1)..].Trim().Trim('"');
                if (k.Length > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)))
                    Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    private static string? FindUp(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string p = Path.Combine(dir.FullName, fileName);
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }
}
