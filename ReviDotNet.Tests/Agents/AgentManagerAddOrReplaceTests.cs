// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Linq;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Locks in <see cref="AgentManagerService.AddOrReplace"/> — the registry mutation that lets the Agent
/// Workshop apply in-memory edits to agents with no writable file on disk (embedded resources). An edit
/// must swap the live profile in place under the same name rather than registering a duplicate, so the
/// next run resolves the modified agent. Adding a brand-new name simply registers it.
/// </summary>
public class AgentManagerAddOrReplaceTests
{
    private static string AgentText(string description) => $"""
        [[information]]
        name = echo

        [[loop]]
        entry = main

        [[state.main]]
        description = {description}

        [[_state.main.instruction]]
        Echo the input.

        [[_loop]]
        main
          -> [end] [when: DONE]
        """;

    private static AgentManagerService NewManager()
        => new(new RecordingReviLogger<AgentManagerService>());

    [Fact]
    public void AddOrReplace_SwapsExistingProfileInPlace_WithoutDuplicating()
    {
        AgentManagerService manager = NewManager();
        manager.Add(AgentBuilder.FromText(AgentText("original")));

        manager.AddOrReplace(AgentBuilder.FromText(AgentText("revised")));

        manager.GetAll().Should().ContainSingle("the edit replaces the agent rather than registering a duplicate")
            .Which.Name.Should().Be("echo");
        manager.Get("echo")!.States.Single().Description.Should().Be("revised",
            "subsequent runs must resolve the edited definition");
    }

    [Fact]
    public void AddOrReplace_RegistersAgent_WhenNoneExistsYet()
    {
        AgentManagerService manager = NewManager();

        manager.AddOrReplace(AgentBuilder.FromText(AgentText("brand new")));

        manager.Get("echo")!.States.Single().Description.Should().Be("brand new");
    }
}
