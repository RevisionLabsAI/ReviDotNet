// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;

namespace Revi.Refinery.Hosting;

/// <summary>Finds the plugin project(s) in a repo: explicit overrides, then a <c>.refinery.json</c> manifest,
/// then convention (any <c>.csproj</c> referencing <c>ReviDotNet.Refinery.Sdk</c>).</summary>
internal static class PluginDiscovery
{
    private const string SdkRefMarker = "ReviDotNet.Refinery.Sdk";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<string> DiscoverProjects(RepoSource repo)
    {
        string root = repo.Path;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        if (repo.Projects is { Count: > 0 })
            return repo.Projects.Select(p => Path.GetFullPath(Path.Combine(root, p))).Where(File.Exists).ToList();

        RefineryManifest? manifest = ReadManifest(root);
        if (manifest?.Projects is { Count: > 0 })
            return manifest.Projects.Select(p => Path.GetFullPath(Path.Combine(root, p))).Where(File.Exists).ToList();

        List<string> hits = [];
        foreach (string csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (File.ReadAllText(csproj).Contains(SdkRefMarker, StringComparison.OrdinalIgnoreCase))
                    hits.Add(csproj);
            }
            catch { /* ignore unreadable */ }
        }
        return hits;
    }

    public static string? ReadManifestBuildConfig(string repoRoot) => ReadManifest(repoRoot)?.BuildConfiguration;

    private static RefineryManifest? ReadManifest(string repoRoot)
    {
        string path = Path.Combine(repoRoot, ".refinery.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RefineryManifest>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
    }
}
