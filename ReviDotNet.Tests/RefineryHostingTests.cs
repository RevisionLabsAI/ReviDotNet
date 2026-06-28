// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Revi.Refinery;
using Revi.Refinery.Hosting;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// End-to-end validation of the plugin host: discover → <c>dotnet build</c> → collectible-ALC load →
/// instantiate, proving the contract type has a single identity across the boundary. Builds the in-repo
/// <c>ReviDotNet.Refinery.TestPlugin</c> via the dotnet CLI (a few seconds).
/// </summary>
public class RefineryHostingTests
{
    [Fact]
    public async Task PluginManager_BuildsLoadsAndExposes_TestPlugin()
    {
        string pluginDir = LocateTestPluginDir();
        IOptions<RefineryHostingOptions> options = Options.Create(new RefineryHostingOptions
        {
            Repos = [new RepoSource { Path = pluginDir }],
            BuildOnStartup = false
        });
        PluginManager manager = new(options, new PluginBuilder(), new PluginLoader());

        await manager.RefreshAllAsync();

        LoadedPlugin loaded = manager.Catalog.Should().ContainSingle().Subject;
        loaded.Status.Should().Be(PluginStatus.Loaded, loaded.Error ?? "(no error)");
        loaded.Plugin.Should().NotBeNull();
        loaded.Name.Should().Be("test-plugin");

        // The loaded instance IS the host's contract type (shared ALC identity), and its Sdk-typed
        // members marshal cleanly across the boundary.
        loaded.Plugin.Should().BeAssignableTo<IRefinementPlugin>();
        loaded.Plugin!.GetInvariantCheckers().Should().ContainSingle().Which.Id.Should().Be("OK-1");
        loaded.Plugin.GetScenarioSuites().Should().ContainSingle();
        loaded.Plugin.GetAgents().Should().ContainSingle().Which.Name.Should().Be("sample-agent");
    }

    private static string LocateTestPluginDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReviDotNet.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run within the ReviDotNet repo");
        return Path.Combine(dir!.FullName, "ReviDotNet.Refinery.TestPlugin");
    }
}
