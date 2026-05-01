// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

public class ToolManagerRegistrationTests
{
    [Fact]
    public void Register_AddsBuiltInTool_ThatIsRetrievableByName()
    {
        string name = $"test-tool-{Guid.NewGuid():n}";
        var tool = new FakeBuiltInTool(name, "ok");

        ToolManager.Register(tool);

        try
        {
            ToolManager.GetBuiltIn(name).Should().BeSameAs(tool);
            ToolManager.GetBuiltInNames().Should().Contain(name);
        }
        finally
        {
            ToolManager.Unregister(name);
        }
    }

    [Fact]
    public void Register_OverwritesExistingToolWithSameName()
    {
        string name = $"test-tool-{Guid.NewGuid():n}";
        var first = new FakeBuiltInTool(name, "first");
        var second = new FakeBuiltInTool(name, "second");

        ToolManager.Register(first);
        ToolManager.Register(second);

        try
        {
            ToolManager.GetBuiltIn(name).Should().BeSameAs(second);
        }
        finally
        {
            ToolManager.Unregister(name);
        }
    }

    [Fact]
    public void Register_RejectsNullToolAndBlankName()
    {
        Action withNull = () => ToolManager.Register(null!);
        withNull.Should().Throw<ArgumentNullException>();

        Action withBlank = () => ToolManager.Register(new FakeBuiltInTool("", "x"));
        withBlank.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unregister_ReturnsTrueForRegisteredAndFalseOtherwise()
    {
        string name = $"test-tool-{Guid.NewGuid():n}";
        ToolManager.Register(new FakeBuiltInTool(name, "ok"));

        ToolManager.Unregister(name).Should().BeTrue();
        ToolManager.Unregister(name).Should().BeFalse();
        ToolManager.GetBuiltIn(name).Should().BeNull();
    }

    [Fact]
    public async Task RegisteredTool_IsActuallyExecutable()
    {
        string name = $"test-tool-{Guid.NewGuid():n}";
        ToolManager.Register(new FakeBuiltInTool(name, "hello world"));

        try
        {
            var tool = ToolManager.GetBuiltIn(name);
            tool.Should().NotBeNull();

            ToolCallResult result = await tool!.ExecuteAsync("ignored", CancellationToken.None);
            result.Failed.Should().BeFalse();
            result.Output.Should().Be("hello world");
        }
        finally
        {
            ToolManager.Unregister(name);
        }
    }
}
