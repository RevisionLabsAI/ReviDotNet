// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Tests for the chat + file-attachment additions:
///   • interaction-mode parses from .agent settings (defaults to fixed)
///   • a seeded conversation (chat turn) replaces the synthesised initial message
///   • the file-access tools read AgentRunContext.Files and are auto-allowed when a run has attachments
/// </summary>
public class ChatAndFileToolsTests
{
    // ── A1: interaction-mode schema ──────────────────────────────────────────

    [Theory]
    [InlineData("both", InteractionMode.Both)]
    [InlineData("chat", InteractionMode.Chat)]
    [InlineData("fixed", InteractionMode.Fixed)]
    public void InteractionMode_ParsesFromSettings(string value, InteractionMode expected)
    {
        AgentProfile profile = AgentBuilder.FromText(AgentText(interactionMode: value));
        profile.InteractionMode.Should().Be(expected);
        profile.EffectiveInteractionMode.Should().Be(expected);
    }

    [Fact]
    public void InteractionMode_DefaultsToFixed_WhenOmitted()
    {
        AgentProfile profile = AgentBuilder.FromText(AgentText(interactionMode: null));
        profile.InteractionMode.Should().BeNull();
        profile.EffectiveInteractionMode.Should().Be(InteractionMode.Fixed);
    }

    // ── A2: seeded chat conversation ─────────────────────────────────────────

    [Fact]
    public async Task SeedHistory_ReplacesInitialMessage_WithPriorConversation()
    {
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", Array.Empty<(string, string)>(), "done") },
            _ => AgentBuilder.FromText(AgentText())!);

        var seed = new List<Message>
        {
            new("user", "PRIOR_QUESTION_AAA"),
            new("assistant", "PRIOR_ANSWER_BBB"),
            new("user", "NEW_TURN_CCC"),
        };

        AgentResult result = await Agent.Run(harness.AgentName, null, AgentRunContext.Root(), default, seed);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        harness.Requests.Should().NotBeEmpty();
        string firstRequest = System.Linq.Enumerable.First(harness.Requests);
        firstRequest.Should().Contain("PRIOR_ANSWER_BBB");   // the whole prior conversation is sent…
        firstRequest.Should().Contain("NEW_TURN_CCC");        // …including the new user turn…
        firstRequest.Should().NotContain("Begin.");           // …instead of the synthesised initial message
    }

    [Fact]
    public async Task NoSeed_NoInputs_SynthesisesBeginMessage()
    {
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", Array.Empty<(string, string)>(), "done") },
            _ => AgentBuilder.FromText(AgentText())!);

        AgentResult result = await Agent.Run(harness.AgentName);

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        System.Linq.Enumerable.First(harness.Requests).Should().Contain("Begin.");
    }

    // ── A5: file-access tools ────────────────────────────────────────────────

    [Fact]
    public async Task ListFilesTool_ReturnsManifest_FromRunContext()
    {
        using var scope = AgentRunContext.Push(AgentRunContext.Root(SampleRegistry()));

        ToolCallResult result = await new ListFilesTool().ExecuteAsync("", CancellationToken.None);

        result.Failed.Should().BeFalse();
        result.Output.Should().Contain("notes.txt");
        result.Output.Should().Contain("pic.png");
        result.Output.Should().Contain("\"isImage\": true");   // the png is flagged as an image
    }

    [Fact]
    public async Task ReadFileTool_NoAttachments_Fails()
    {
        using var scope = AgentRunContext.Push(AgentRunContext.Root());   // no files

        ToolCallResult result = await new ReadFileTool(new StubModels()).ExecuteAsync("{\"file\":\"x\"}", CancellationToken.None);

        result.Failed.Should().BeTrue();
        result.ErrorMessage.Should().Contain("No files");
    }

    [Fact]
    public async Task ReadFileTool_UnknownFile_FailsWithAvailableList()
    {
        using var scope = AgentRunContext.Push(AgentRunContext.Root(SampleRegistry()));

        ToolCallResult result = await new ReadFileTool(new StubModels()).ExecuteAsync("{\"file\":\"missing.doc\"}", CancellationToken.None);

        result.Failed.Should().BeTrue();
        result.ErrorMessage.Should().Contain("notes.txt");   // lists what *is* available
    }

    [Fact]
    public async Task FileTools_AreAutoAllowed_WhenAttachmentsPresent()
    {
        bool invoked = false;
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new[] { ("read-file", "{\"file\":\"notes.txt\"}") }, "reading") },
            _ => AgentBuilder.FromText(AgentText())!);   // note: state lists NO tools
        harness.RegisterTool(new FakeBuiltInTool("read-file", _ => { invoked = true; return "ok"; }));

        AgentResult result = await Agent.Run(harness.AgentName, null, AgentRunContext.Root(SampleRegistry()));

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        invoked.Should().BeTrue();   // allowed purely because the run has attachments
    }

    [Fact]
    public async Task FileTools_AreNotAllowed_WithoutAttachments()
    {
        bool invoked = false;
        using var harness = new AgentTestHarness(
            new[] { new FakeAgentTurn("DONE", new[] { ("read-file", "{\"file\":\"notes.txt\"}") }, "reading") },
            _ => AgentBuilder.FromText(AgentText())!);
        harness.RegisterTool(new FakeBuiltInTool("read-file", _ => { invoked = true; return "ok"; }));

        AgentResult result = await Agent.Run(harness.AgentName, null, AgentRunContext.Root());   // no files

        result.ExitReason.Should().Be(AgentExitReason.Completed);
        invoked.Should().BeFalse();   // the read-file call is disallowed and dropped
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static SessionFileRegistry SampleRegistry() => new(new List<SessionFile>
    {
        new() { Id = "f1", Name = "notes.txt", MediaType = "text/plain", Bytes = Encoding.UTF8.GetBytes("hello world") },
        new() { Id = "f2", Name = "pic.png", MediaType = "image/png", Bytes = new byte[] { 1, 2, 3 } },
    });

    // A minimal one-state agent that lists no tools (so file tools are only reachable via auto-allow).
    private static string AgentText(string? interactionMode = null)
    {
        string settings = interactionMode is null ? "" : $"\n[[settings]]\ninteraction-mode = {interactionMode}\n";
        return $@"
[[information]]
name = unused
{settings}
[[loop]]
entry = work

[[state.work]]
description = work state

[[_state.work.instruction]]
Do the thing, then signal DONE.

[[_loop]]
work
  -> [end] [when: DONE]
";
    }

    /// <summary>An IModelManager with no usable models — read-file error-path tests never reach the reader.</summary>
    private sealed class StubModels : IModelManager
    {
        public Task LoadAsync(System.Reflection.Assembly assembly, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LoadDirectory(string rootDirectory) { }
        public ModelProfile? Get(string name) => null;
        public List<ModelProfile> GetAll() => new();
        public ModelProfile? Find(string? minTier, bool needsPromptCompletion = false) => null;
        public ModelProfile? Find(string? minTier, bool needsPromptCompletion, List<string>? blockedModels) => null;
        public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false) => null;
        public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion, List<string>? blockedModels) => null;
        public void Add(ModelProfile model) { }
    }
}
