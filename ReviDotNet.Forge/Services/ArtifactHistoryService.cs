// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// One archived version of an artifact (prompt or agent).
/// </summary>
public sealed class ArtifactVersion
{
    public required string ArtifactName { get; init; }
    public required string Kind { get; init; } // "prompt" | "agent"
    public required DateTime SavedAt { get; init; }
    public required string Content { get; init; }
    /// <summary>Absolute path of the archived snapshot on disk.</summary>
    public required string ArchivePath { get; init; }
}

/// <summary>
/// Persists historical snapshots of prompts and agents under a `.history` sibling folder,
/// keyed by artifact name + UTC timestamp. Used by the version history UI in the Prompts
/// and Agents registries.
///
/// Layout:
///   RConfigs/.history/prompts/&lt;Folder.Name&gt;/&lt;UTC-yyyyMMddTHHmmssZ&gt;.pmt
///   RConfigs/.history/agents/&lt;folder.name&gt;/&lt;UTC-yyyyMMddTHHmmssZ&gt;.agent
///
/// Snapshotting is best-effort — failures are logged via the host but do not throw,
/// because losing a snapshot must never prevent the main save from succeeding.
/// </summary>
public sealed class ArtifactHistoryService
{
    private readonly string _historyRoot;
    private readonly ILogger<ArtifactHistoryService>? _log;

    public ArtifactHistoryService(IConfiguration configuration, ILogger<ArtifactHistoryService>? log = null)
    {
        // RConfigs/.history sits next to RConfigs/Prompts and RConfigs/Agents.
        // Use the prompts source path's parent as the convention root, falling back to RConfigs/.
        string promptsPath = configuration["Forge:PromptsSourcePath"] ?? "RConfigs/Prompts";
        string root = Path.GetDirectoryName(promptsPath) ?? "RConfigs";
        _historyRoot = Path.Combine(root, ".history");
        _log = log;
    }

    /// <summary>
    /// Saves a snapshot of the current content for the given artifact, before it is overwritten on disk.
    /// Safe to call before every save — failures are swallowed and logged.
    /// </summary>
    public void Snapshot(string kind, string artifactName, string content)
    {
        if (string.IsNullOrWhiteSpace(artifactName) || content is null) return;

        try
        {
            string ext = kind == "prompt" ? ".pmt" : ".agent";
            string dir = ResolveArtifactDirectory(kind, artifactName);
            Directory.CreateDirectory(dir);
            string filename = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ext;
            File.WriteAllText(Path.Combine(dir, filename), content);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to snapshot {Kind} '{Name}'", kind, artifactName);
        }
    }

    /// <summary>
    /// Returns all archived versions for an artifact, newest first.
    /// </summary>
    public List<ArtifactVersion> ListVersions(string kind, string artifactName)
    {
        var results = new List<ArtifactVersion>();
        string dir = ResolveArtifactDirectory(kind, artifactName);
        if (!Directory.Exists(dir)) return results;

        string ext = kind == "prompt" ? "*.pmt" : "*.agent";
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, ext))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (!DateTime.TryParseExact(stem, "yyyyMMddTHHmmssfffZ",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var saved))
                {
                    saved = File.GetCreationTimeUtc(file);
                }

                string content;
                try { content = File.ReadAllText(file); }
                catch { continue; }

                results.Add(new ArtifactVersion
                {
                    ArtifactName = artifactName,
                    Kind = kind,
                    SavedAt = saved,
                    Content = content,
                    ArchivePath = file
                });
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to list versions for {Kind} '{Name}'", kind, artifactName);
        }

        return results.OrderByDescending(v => v.SavedAt).ToList();
    }

    private string ResolveArtifactDirectory(string kind, string artifactName)
    {
        // Mirror the on-disk folder layout: prompt 'A.B.C' → A/B/C; agent 'a/b' → a/b
        string subfolder = kind == "prompt"
            ? artifactName.Replace('.', Path.DirectorySeparatorChar)
            : artifactName.Replace('/', Path.DirectorySeparatorChar);
        string kindFolder = kind == "prompt" ? "prompts" : "agents";
        return Path.Combine(_historyRoot, kindFolder, subfolder);
    }
}
