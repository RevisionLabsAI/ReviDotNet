// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Services.Observer;

public interface IReviLogLimiter
{
    IReadOnlyDictionary<string, Revi.LogLevel> Entries { get; }
    IReadOnlyDictionary<string, bool> SiteSuppressEntries { get; }
    event Action? Changed;

    bool IsSuppressed(RlogEvent e);

    void Set(string key, Revi.LogLevel minLevel);
    void Remove(string key);
    void ReplaceAll(IEnumerable<KeyValuePair<string, Revi.LogLevel>> entries);
    void ReplaceAll(IEnumerable<KeyValuePair<string, Revi.LogLevel>> entries, IEnumerable<string> siteKeys);

    void SuppressSite(string siteKey);
    void RemoveSite(string siteKey);

    Task<bool> LoadAsync(CancellationToken ct = default);
    Task<bool> SaveAsync(CancellationToken ct = default);
}
