// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery.Hosting;

/// <summary>Configuration for the plugin host: which local repos to build + load, and how.</summary>
public sealed class RefineryHostingOptions
{
    /// <summary>Configuration section name (bind from <c>Refinery</c>).</summary>
    public const string SectionName = "Refinery";

    /// <summary>Local repo directories to scan for refinement plugins.</summary>
    public List<RepoSource> Repos { get; set; } = [];

    /// <summary>Default build configuration (Debug/Release) when a repo doesn't override it.</summary>
    public string BuildConfiguration { get; set; } = "Debug";

    /// <summary>
    /// Target framework moniker to build/resolve (F11). Disambiguates the output assembly when a plugin
    /// targets multiple frameworks (<c>&lt;TargetFrameworks&gt;</c>): passed as <c>-p:TargetFramework={tfm}</c>
    /// to both the build and the TargetPath resolution so they agree.
    /// </summary>
    public string TargetFramework { get; set; } = "net9.0";

    /// <summary>Discover/build/load all configured repos at startup.</summary>
    public bool BuildOnStartup { get; set; } = true;

    /// <summary>
    /// Opt-in (Phase 6): when true, after the initial refresh the host watches each repo's
    /// <c>*.cs</c>/<c>*.csproj</c> files and rebuilds+reloads the affected plugin (debounced). Default FALSE
    /// so nothing auto-rebuilds unless explicitly enabled.
    /// </summary>
    public bool WatchForChanges { get; set; } = false;

    /// <summary>Debounce window (ms) before a file-change burst triggers a reload. Default 500ms.</summary>
    public int WatchDebounceMs { get; set; } = 500;
}

/// <summary>A single local repo to load plugins from.</summary>
public sealed class RepoSource
{
    /// <summary>Absolute path to the repo root (or any directory containing the plugin project).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Optional per-repo build configuration override.</summary>
    public string? BuildConfiguration { get; set; }

    /// <summary>Optional per-repo target framework override (F11); falls back to the global default.</summary>
    public string? TargetFramework { get; set; }

    /// <summary>Optional explicit plugin project paths (relative to <see cref="Path"/>); overrides discovery.</summary>
    public List<string>? Projects { get; set; }
}

/// <summary>Optional <c>.refinery.json</c> manifest at a repo root.</summary>
public sealed class RefineryManifest
{
    /// <summary>Plugin project paths (relative to the repo root).</summary>
    public List<string>? Projects { get; set; }

    /// <summary>Build configuration override.</summary>
    public string? BuildConfiguration { get; set; }
}
