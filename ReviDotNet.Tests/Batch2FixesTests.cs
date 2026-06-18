// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 2 audit fixes (D14, D17, D18, D19).
/// </summary>
public class Batch2FixesTests
{
    // ── D14: guidance-schema-type = defer maps to the provider-default deferral strategy ──

    [Fact]
    public void ConvertToType_Defer_MapsToDefaultGuidanceStrategy()
    {
        object? result = RConfigParser.ConvertToType("defer", typeof(GuidanceSchemaType));
        result.Should().Be(GuidanceSchemaType.Default);
    }

    [Fact]
    public void PromptToObject_GuidanceDefer_SetsDefaultStrategy()
    {
        var data = new Dictionary<string, string>
        {
            ["information_name"] = "p",
            ["information_version"] = "1",
            ["_system"] = "You are helpful.",
            ["settings_guidance-schema-type"] = "defer"
        };

        Prompt prompt = Prompt.ToObject(data);
        prompt.GuidanceSchema.Should().Be(GuidanceSchemaType.Default);
    }

    // ── D17: model instruction inputs default to Listed (so instructions receive inputs) ──

    [Fact]
    public void ModelProfile_DefaultInstructionInputType_IsListed()
    {
        new ModelProfile().DefaultInstructionInputType.Should().Be(InputType.Listed);
    }

    // ── D18: a prompt with no version defaults to 1 instead of being dropped at load ──

    [Fact]
    public void PromptToObject_NoVersion_DefaultsToOne()
    {
        var data = new Dictionary<string, string>
        {
            ["information_name"] = "p",
            ["_system"] = "You are helpful."
            // no information_version
        };

        Prompt prompt = Prompt.ToObject(data);
        prompt.Name.Should().Be("p");          // prompt is NOT dropped
        prompt.Version.Should().Be(1);         // version defaulted to 1
    }

    // ── D19: secrets are redacted before reaching the event sink (message + serialized objects) ──

    [Fact]
    public void Log_RedactsSecretsInMessageAndObjectsBeforePublishing()
    {
        var publisher = new CapturingPublisher();
        IConfiguration config = new ConfigurationBuilder().Build();
        var logger = new ReviLogger(publisher, config);

        logger.LogInfo(
            "calling https://api.example.com/v1?key=SUPERSECRET123 now",
            object1: new { url = "https://x/?api_key=TOKEN999" });

        RlogEvent published = publisher.Events.Should().ContainSingle().Subject;

        published.Message.Should().NotContain("SUPERSECRET123");
        published.Message.Should().Contain("key=***");
        published.Object1.Should().NotBeNull();
        published.Object1!.Should().NotContain("TOKEN999");
        published.Object1!.Should().Contain("api_key=***");
    }

    private sealed class CapturingPublisher : IRlogEventPublisher
    {
        public List<RlogEvent> Events { get; } = new();

        public void PublishLogEvent(RlogEvent rlogEvent) => Events.Add(rlogEvent);

        public Task PublishLogEventAsync(RlogEvent rlogEvent)
        {
            Events.Add(rlogEvent);
            return Task.CompletedTask;
        }
    }
}
