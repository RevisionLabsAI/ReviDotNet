// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;
using System.Xml.Linq;

namespace Revi.Refinery.Hosting;

/// <summary>Finds the plugin project(s) in a repo: explicit overrides, then a <c>.refinery.json</c> manifest,
/// then convention (any <c>.csproj</c> referencing <c>ReviDotNet.Refinery.Sdk</c>).</summary>
internal static class PluginDiscovery
{
    /// <summary>The Sdk identity a plugin must reference (csproj file name without extension / package id).</summary>
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
                if (ReferencesSdk(csproj))
                    hits.Add(csproj);
            }
            catch { /* ignore unreadable / malformed */ }
        }
        return hits;
    }

    /// <summary>
    /// True iff the csproj has an EXACT <c>ProjectReference</c> or <c>PackageReference</c> to the Sdk (F10).
    /// We parse the project as XML and compare the reference's identity — the <c>ProjectReference Include</c>
    /// path's file name (without <c>.csproj</c>) or the <c>PackageReference Include</c> package id — to
    /// <see cref="SdkRefMarker"/> (case-insensitive). This rejects substring false-positives like comments,
    /// namespaces, or sister packages such as <c>ReviDotNet.Refinery.Sdk.Extras</c>.
    /// </summary>
    private static bool ReferencesSdk(string csprojPath)
    {
        XDocument doc = XDocument.Load(csprojPath);

        foreach (XElement reference in doc.Descendants())
        {
            string local = reference.Name.LocalName;
            bool isProjectRef = string.Equals(local, "ProjectReference", StringComparison.OrdinalIgnoreCase);
            bool isPackageRef = string.Equals(local, "PackageReference", StringComparison.OrdinalIgnoreCase);
            if (!isProjectRef && !isPackageRef)
                continue;

            string? include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;

            if (isPackageRef)
            {
                // PackageReference Include is the package id verbatim.
                if (string.Equals(include.Trim(), SdkRefMarker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                // ProjectReference Include is a (possibly relative, possibly back-slashed) path to a .csproj;
                // compare its file name without extension.
                string fileName = Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar).Trim());
                if (string.Equals(fileName, SdkRefMarker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
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
