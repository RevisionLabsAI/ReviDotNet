// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Tests for the high-level <see cref="Infer"/> facade (<c>Infer.Completion</c> /
/// <c>Infer.CompletionStream</c>): it resolves the completion type, builds the prompt/messages from a
/// <see cref="Prompt"/> + <see cref="ModelProfile"/>, dispatches through the provider's client, and
/// parses the result. Complements <see cref="InferClientTests"/> (which drives the lower-level
/// <see cref="InferClient"/> directly) by covering the facade layer against the in-memory
/// <see cref="FakeInferenceServer"/>.
/// </summary>
public class InferFacadeTests
{
    /// <summary>
    /// Builds a provider + model wired to an in-memory <see cref="FakeInferenceServer"/>. The provider
    /// constructor auto-creates an <see cref="ProviderProfile.InferenceClient"/> backed by a real
    /// <c>HttpClient</c> (which cannot reach the in-memory server), so it is replaced with one backed by
    /// the TestServer's handler via <c>httpClientOverride</c> — mirroring <see cref="InferClientTests"/>.
    /// </summary>
    private static (ProviderProfile Provider, ModelProfile Model, TestServer Server) CreateProviderAndModel(
        Protocol protocol,
        bool supportsCompletion,
        string modelName = "test-model",
        string providerName = "test-provider",
        string apiKey = "test-key")
    {
        var (server, baseAddress) = FakeInferenceServer.Create();
        var httpClient = server.CreateClient();
        httpClient.BaseAddress = baseAddress;

        var provider = new ProviderProfile(
            name: providerName,
            enabled: true,
            protocol: protocol,
            apiURL: baseAddress.ToString(),
            apiKey: apiKey,
            supportsCompletion: supportsCompletion,
            supportsGuidance: false,
            defaultModel: modelName);

        // Point the provider's client at the in-memory TestServer (a plain HttpClient can't reach it).
        provider.InferenceClient = new InferClient(
            apiUrl: baseAddress.ToString(),
            apiKey: apiKey,
            protocol: protocol,
            defaultModel: modelName,
            timeoutSeconds: 30,
            delayBetweenRequestsMs: 0,
            retryAttemptLimit: 1,
            retryInitialDelaySeconds: 0,
            simultaneousRequests: 2,
            supportsCompletion: supportsCompletion,
            supportsGuidance: false,
            defaultGuidanceType: GuidanceType.Disabled,
            defaultGuidanceString: null,
            httpClientOverride: httpClient);

        var model = new ModelProfile
        {
            Name = modelName + "-profile",
            Enabled = true,
            ModelString = modelName,
            ProviderName = providerName,
            Provider = provider,
            Tier = ModelTier.C,
            TokenLimit = 10_000,
            // Prompt-completion template pieces sufficient for CompletionPrompt.BuildString.
            Structure = "{system}{instruction}{input}{example}{output}",
            SystemSection = "SYS: {content}\n",
            InstructionSection = "INS: {content}\n",
            InputSection = "INP:\n{list}\n",
            ExampleSection = "EXAMPLES\n{content}\n",
            ExampleStructure = "Example #{iterator}:\n{exsystem}{exinstruction}{exinput}{exoutput}\n",
            ExampleSubSystem = "- System (#{iterator}):\n{content}\n\n",
            ExampleSubInstruction = "- Instruction (#{iterator}):\n{content}\n\n",
            ExampleSubInput = "- Input (#{iterator}):\n{content}\n\n",
            ExampleSubOutput = "- Output (#{iterator}):\n{content}\n\n",
            OutputSection = "OUT:",
            InputItem = "{iterator}. {label}: {text}\n",
            InputItemMulti = "{iterator}. {label}: {text}\n",
            DefaultSystemInputType = InputType.Listed,
            DefaultInstructionInputType = InputType.Listed,
            // Chat message switches for CompletionChat.BuildMessages.
            SystemMessage = true,
            PromptInSystem = false,
            SystemInUser = false,
            PromptInUser = true
        };

        return (provider, model, server);
    }

    /// <summary>Builds a minimal prompt with the given completion type.</summary>
    private static Prompt MakePrompt(string completionType, string? system = "You are helpful", string? instruction = "Say hello")
        => new Prompt
        {
            Name = "unit-test-prompt",
            CompletionType = completionType,
            System = system,
            Instruction = instruction,
            FewShotExamples = 0,
            RequestJson = false
        };

    [Fact]
    public async Task Completion_PromptOnly_vLLM_ReturnsParsedText()
    {
        var (_, model, server) = CreateProviderAndModel(Protocol.vLLM, supportsCompletion: true, modelName: "test-model");
        using var __ = server;

        Prompt prompt = MakePrompt("PromptOnly");

        CompletionResult? response = await Infer.Completion(prompt, inputs: null, modelProfile: model);

        response.Should().NotBeNull();
        response!.Selected.Should().Be("Hello world (prompt)");
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task Completion_ChatOnly_OpenAI_ReturnsParsedMessage()
    {
        var (_, model, server) = CreateProviderAndModel(Protocol.OpenAI, supportsCompletion: false, modelName: "gpt-like");
        using var __ = server;

        Prompt prompt = MakePrompt("ChatOnly");

        CompletionResult? response = await Infer.Completion(prompt, inputs: null, modelProfile: model);

        response.Should().NotBeNull();
        response!.Selected.Should().Be("Hello world (chat)");
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task CompletionStream_PromptOnly_Gemini_YieldsStreamedChunks()
    {
        var (_, model, server) = CreateProviderAndModel(Protocol.Gemini, supportsCompletion: true, modelName: "gemini-pro", apiKey: "abc123");
        using var __ = server;

        Prompt prompt = MakePrompt("PromptOnly");

        List<string> chunks = new();
        await foreach (string chunk in Infer.CompletionStream(prompt, inputs: null, modelProfile: model))
        {
            chunks.Add(chunk);
        }

        // The fake Gemini stream endpoint emits two SSE chunks.
        chunks.Should().Equal("chunk1", "chunk2");
    }
}
