// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Optional configuration consumed by <see cref="ReviServiceCollectionExtensions.AddReviDotNet"/> and
/// applied at startup by the registry initializer.
/// </summary>
public sealed class ReviRegistryOptions
{
    /// <summary>
    /// Additional folders to load RConfigs from <em>after</em> the application's own (embedded or on-disk)
    /// set. Each entry is treated as an <c>RConfigs</c> root and may contain any of the standard subfolders —
    /// <c>Providers/</c>, <c>Models/Inference/</c>, <c>Models/Embedding/</c>, <c>Prompts/</c>, <c>Agents/</c>;
    /// missing subfolders are skipped. Entries already loaded (by name) are <em>not</em> overwritten, so the
    /// application's own configs win on a name clash. Relative paths resolve against the current working
    /// directory. Lets a host point Forge at agents/models kept in a separate project for testing.
    /// </summary>
    public List<string> AdditionalConfigDirectories { get; } = new();
}
