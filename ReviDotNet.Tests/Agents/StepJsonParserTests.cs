// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Regression tests for <see cref="StepJsonParser"/> — the tolerant parser that lets providers
/// without enforced structured output (e.g. Claude) succeed. It must recover the AgentStepResponse
/// JSON from clean output, ```json fences, and surrounding prose, while still rejecting non-JSON.
/// </summary>
public class StepJsonParserTests
{
    [Fact]
    public void Parses_CleanJson()
    {
        var r = StepJsonParser.Parse("""{"signal":"DONE","tool_calls":[],"content":"hi","thinking":null}""");
        r.Should().NotBeNull();
        r!.Signal.Should().Be("DONE");
        r.Content.Should().Be("hi");
        r.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public void Parses_MarkdownFencedJson()
    {
        string raw = "```json\n{\"signal\":\"CONTINUE\",\"tool_calls\":[],\"content\":\"working\"}\n```";
        var r = StepJsonParser.Parse(raw);
        r.Should().NotBeNull();
        r!.Signal.Should().Be("CONTINUE");
        r.Content.Should().Be("working");
    }

    [Fact]
    public void Parses_FenceWithoutLanguage()
    {
        string raw = "```\n{\"signal\":null,\"tool_calls\":[],\"content\":\"ok\"}\n```";
        var r = StepJsonParser.Parse(raw);
        r.Should().NotBeNull();
        r!.Signal.Should().BeNull();
        r.Content.Should().Be("ok");
    }

    [Fact]
    public void Parses_JsonWrappedInProse()
    {
        string raw = "Sure! Here is my response:\n{\"signal\":\"DONE\",\"tool_calls\":[],\"content\":\"done\"}\nLet me know if you need more.";
        var r = StepJsonParser.Parse(raw);
        r.Should().NotBeNull();
        r!.Signal.Should().Be("DONE");
        r.Content.Should().Be("done");
    }

    [Fact]
    public void Parses_ToolCalls()
    {
        string raw = "```json\n{\"signal\":null,\"tool_calls\":[{\"name\":\"web-search\",\"input\":\"{\\\"q\\\":\\\"x\\\"}\"}],\"content\":\"\"}\n```";
        var r = StepJsonParser.Parse(raw);
        r.Should().NotBeNull();
        r!.ToolCalls.Should().HaveCount(1);
        r.ToolCalls[0].Name.Should().Be("web-search");
    }

    [Theory]
    [InlineData("I'm sorry, I can't help with that.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Returns_Null_ForNonJson(string? raw)
    {
        StepJsonParser.Parse(raw).Should().BeNull();
    }
}
