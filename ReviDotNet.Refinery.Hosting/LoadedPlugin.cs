// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Runtime.Loader;

namespace Revi.Refinery.Hosting;

/// <summary>Lifecycle status of a discovered plugin project.</summary>
public enum PluginStatus
{
    Discovered,
    Building,
    BuildFailed,
    Loaded,
    LoadFailed
}

/// <summary>
/// A discovered (and possibly built + loaded) plugin from a local repo.
/// <para>
/// IMMUTABLE by design (F12): every state transition produces a NEW record that
/// <see cref="PluginManager"/> atomically swaps into its catalog under lock. Readers
/// (RefineryApiEndpoints.ToDto, RefineryCampaignService, Refinery.razor) only ever READ the
/// properties below, so they never observe a half-written instance. The public property NAMES are
/// preserved exactly for those readers.
/// </para>
/// </summary>
public sealed record LoadedPlugin
{
    /// <summary>The repo the plugin came from.</summary>
    public required string RepoPath { get; init; }

    /// <summary>The plugin project (.csproj) path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>The built assembly path (null until built).</summary>
    public string? AssemblyPath { get; init; }

    /// <summary>The loaded plugin instance (null unless <see cref="Status"/> is <see cref="PluginStatus.Loaded"/>).</summary>
    public IRefinementPlugin? Plugin { get; init; }

    /// <summary>Current status.</summary>
    public PluginStatus Status { get; init; } = PluginStatus.Discovered;

    /// <summary>Build/load error text, if any.</summary>
    public string? Error { get; init; }

    /// <summary>A non-fatal warning (e.g. ReviDotNet version skew vs the host).</summary>
    public string? Warning { get; init; }

    /// <summary>When the plugin was last loaded.</summary>
    public DateTime? LoadedAt { get; init; }

    /// <summary>The plugin's display name once loaded; else the project file name.</summary>
    public string Name => Plugin?.Name ?? System.IO.Path.GetFileNameWithoutExtension(ProjectPath);

    /// <summary>The collectible load context (internal; used for reload/unload).</summary>
    internal AssemblyLoadContext? Context { get; init; }
}
