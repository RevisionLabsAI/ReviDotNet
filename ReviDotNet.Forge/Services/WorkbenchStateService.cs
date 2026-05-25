// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Process-wide cache that lets pages persist in-progress workflow state across
/// navigation events. Keyed by (page, artifact) so each prompt/agent keeps its own
/// scratchpad. Values are arbitrary CLR objects — pages cast to their own DTO.
///
/// Singleton intentionally (not scoped per circuit) so reconnects and component
/// remounts inside the same browser session find the work-in-progress.
/// </summary>
public sealed class WorkbenchStateService
{
    private readonly ConcurrentDictionary<string, object> _store = new(StringComparer.Ordinal);

    /// <summary>Retrieve previously-stashed state for a (page, artifact) pair, or null.</summary>
    public T? Get<T>(string page, string artifactName) where T : class
    {
        if (string.IsNullOrEmpty(artifactName)) return null;
        return _store.TryGetValue(BuildKey(page, artifactName), out var v) ? v as T : null;
    }

    /// <summary>Store state for a (page, artifact) pair; overwrites prior value.</summary>
    public void Set<T>(string page, string artifactName, T value) where T : class
    {
        if (string.IsNullOrEmpty(artifactName)) return;
        _store[BuildKey(page, artifactName)] = value;
    }

    /// <summary>Removes the cached state for a (page, artifact) pair.</summary>
    public void Clear(string page, string artifactName)
    {
        if (string.IsNullOrEmpty(artifactName)) return;
        _store.TryRemove(BuildKey(page, artifactName), out _);
    }

    private static string BuildKey(string page, string artifactName) => page + "" + artifactName;
}
