using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests;

public class InferTests
{
    private static (ProviderProfile Provider, ModelProfile Model, TestServer Server) CreateProviderAndModel(
        Protocol protocol,
        bool supportsCompletion,
        string modelName = "test-model",
        string providerName = "test-provider",
        string apiKey = "test-key")
    {
        var (server, baseAddress) = FakeInferenceServer.Create();

        // Build provider pointing to Fake server
        var provider = new ProviderProfile(
            name: providerName,
            enabled: true,
            protocol: protocol,
            apiURL: baseAddress.ToString(),
            apiKey: apiKey,
            supportsCompletion: supportsCompletion,
            supportsGuidance: false,
            defaultModel: modelName
        );

        // Build a simple model template sufficient for CompletionPrompt and CompletionChat
        var model = new ModelProfile
        {
            Name = modelName + "-profile",
            Enabled = true,
            ModelString = modelName,
            ProviderName = providerName,
            Provider = provider,
            Tier = ModelTier.C,
            TokenLimit = 10_000,
            // Prompt template pieces for CompletionPrompt
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
            // Input formatting
            SystemInputType = InputType.Listed,
            InstructionInputType = InputType.Listed,
            InputItem = "{iterator}. {label}: {text}\n",
            InputItemMulti = "{iterator}. {label}: {text}\n",
            // Chat message switches
            SystemMessage = true,
            PromptInSystem = false,
            SystemInUser = false,
            PromptInUser = true
        };

        return (provider, model, server);
    }

    private static Prompt MakePrompt(string completionType, string? system = "You are helpful", string? instruction = "Say hello")
    {
        return new Prompt
        {
            Name = "unit-test-prompt",
            CompletionType = completionType,
            System = system,
            Instruction = instruction,
            // Keep other knobs default/minimal
            FewShotExamples = 0,
            RequestJson = false
        };
    }

    [Fact]
    public async Task Completion_PromptOnly_vLLM_ReturnsParsedText()
    {
        var (provider, model, server) = CreateProviderAndModel(Protocol.vLLM, supportsCompletion: true, modelName: "test-model");
        using var _ = server; // dispose with test

        var prompt = MakePrompt("PromptOnly");

        var response = await Infer.Completion(prompt, inputs: null, modelProfile: model);

        response.Should().NotBeNull();
        response!.Selected.Should().Be("Hello world (prompt)");
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task Completion_ChatOnly_OpenAI_ReturnsParsedMessage()
    {
        var (provider, model, server) = CreateProviderAndModel(Protocol.OpenAI, supportsCompletion: false, modelName: "gpt-like");
        using var _ = server;

        var prompt = MakePrompt("ChatOnly");

        var response = await Infer.Completion(prompt, inputs: null, modelProfile: model);

        response.Should().NotBeNull();
        response!.Selected.Should().Be("Hello world (chat)");
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task CompletionStream_Prompt_Gemini_YieldsSingleChunk()
    {
        var (provider, model, server) = CreateProviderAndModel(Protocol.Gemini, supportsCompletion: true, modelName: "gemini-pro", apiKey: "abc123");
        using var _ = server;

        var prompt = MakePrompt("PromptOnly");

        var chunks = new List<string>();
        await foreach (var chunk in Infer.CompletionStream(prompt, inputs: null, modelProfile: model))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(1);
        chunks[0].Should().Be("chunk");
    }

    [Fact]
    public void ListInputs_BuildsExpectedString()
    {
        var (provider, model, server) = CreateProviderAndModel(Protocol.OpenAI, supportsCompletion: false);
        using var _ = server;

        var inputs = new List<Input>
        {
            new Input("A", "Alpha"),
            new Input("B", "Beta")
        };

        var list = Infer.ListInputs(model, inputs);

        list.Should().NotBeNull();
        list!.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Should().HaveCount(2);
        list.Should().Contain("1. A: Alpha");
        list.Should().Contain("2. B: Beta");
    }
}
