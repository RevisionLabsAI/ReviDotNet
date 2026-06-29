// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Revi.Refinery.Hosting;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Phase 6 hosting hardening: F10 discovery exact-match (no substring false-positives) and F9 lease pinning
/// (a held lease defers unload). Deterministic and self-contained — F10 uses synthetic csproj files in a temp
/// dir; F9 builds the in-repo <c>ReviDotNet.Refinery.TestPlugin</c> via the dotnet CLI (a few seconds).
/// </summary>
public class RefineryHostingHardeningTests
{
    // -------- F10: discovery exact-match rejects substring-only mentions --------

    [Fact]
    public void Discovery_RejectsCsproj_ThatOnlySubstringMentionsTheSdk()
    {
        string repo = NewTempRepo();
        try
        {
            // (a) a comment mentioning the Sdk, (b) a sister package whose id merely starts with the marker,
            // (c) a namespace-y string. NONE is an exact ProjectReference/PackageReference to the Sdk.
            WriteCsproj(repo, "Decoy", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <!-- This project talks to ReviDotNet.Refinery.Sdk at runtime but does not reference it. -->
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="ReviDotNet.Refinery.Sdk.Extras" Version="1.0.0" />
                    <ProjectReference Include="../Other/ReviDotNet.Refinery.SdkHelpers.csproj" />
                  </ItemGroup>
                </Project>
                """);

            IReadOnlyList<string> hits = PluginDiscovery.DiscoverProjects(new RepoSource { Path = repo });

            hits.Should().BeEmpty("a substring/sister-package mention is not an exact Sdk reference");
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public void Discovery_AcceptsCsproj_WithExactProjectReferenceToSdk()
    {
        string repo = NewTempRepo();
        try
        {
            WriteCsproj(repo, "RealPlugin", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\..\ReviDotNet.Refinery.Sdk\ReviDotNet.Refinery.Sdk.csproj" />
                  </ItemGroup>
                </Project>
                """);

            IReadOnlyList<string> hits = PluginDiscovery.DiscoverProjects(new RepoSource { Path = repo });

            hits.Should().ContainSingle()
                .Which.Should().EndWith("RealPlugin.csproj");
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public void Discovery_AcceptsCsproj_WithExactPackageReferenceToSdk()
    {
        string repo = NewTempRepo();
        try
        {
            WriteCsproj(repo, "PkgPlugin", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="ReviDotNet.Refinery.Sdk" Version="1.2.3" />
                  </ItemGroup>
                </Project>
                """);

            IReadOnlyList<string> hits = PluginDiscovery.DiscoverProjects(new RepoSource { Path = repo });

            hits.Should().ContainSingle().Which.Should().EndWith("PkgPlugin.csproj");
        }
        finally { TryDelete(repo); }
    }

    // -------- F9: a held lease defers unload/reload --------

    [Fact]
    public async Task Acquire_PinsPlugin_SoReloadDefersUntilLeaseDisposed()
    {
        string pluginDir = LocateTestPluginDir();
        IOptions<RefineryHostingOptions> options = Options.Create(new RefineryHostingOptions
        {
            Repos = [new RepoSource { Path = pluginDir }],
            BuildOnStartup = false,
            WatchForChanges = false
        });
        using PluginManager manager = new(options, new PluginBuilder(), new PluginLoader());

        await manager.RefreshAllAsync();
        LoadedPlugin loaded = manager.Catalog.Should().ContainSingle().Subject;
        loaded.Status.Should().Be(PluginStatus.Loaded, loaded.Error ?? "(no error)");
        string name = loaded.Name;

        // Pin the plugin, then ask for a reload. The reload must NOT complete while the lease is held,
        // because UnloadInternal waits for zero active leases before tearing the ALC down.
        IDisposable lease = manager.Acquire(name);
        Task reload = manager.ReloadAsync(name);

        Task firstDone = await Task.WhenAny(reload, Task.Delay(1500));
        firstDone.Should().NotBeSameAs(reload, "the reload must be blocked while a lease pins the plugin");
        reload.IsCompleted.Should().BeFalse();

        // Releasing the lease lets the reload proceed to completion.
        lease.Dispose();
        await reload.WaitAsync(TimeSpan.FromSeconds(60));
        reload.IsCompletedSuccessfully.Should().BeTrue();

        manager.Catalog.Should().ContainSingle()
            .Which.Status.Should().Be(PluginStatus.Loaded);
    }

    [Fact]
    public void Acquire_ReturnsNoopLease_WhenPluginAbsent()
    {
        IOptions<RefineryHostingOptions> options = Options.Create(new RefineryHostingOptions
        {
            Repos = [],
            BuildOnStartup = false
        });
        using PluginManager manager = new(options, new PluginBuilder(), new PluginLoader());

        IDisposable lease = manager.Acquire("does-not-exist");

        lease.Should().NotBeNull();
        // Disposing a no-op lease is safe and idempotent.
        lease.Dispose();
        lease.Dispose();
    }

    // -------- helpers --------

    private static string NewTempRepo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "refinery-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteCsproj(string repoRoot, string projectName, string contents)
    {
        string projDir = Path.Combine(repoRoot, projectName);
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, projectName + ".csproj"), contents);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
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
