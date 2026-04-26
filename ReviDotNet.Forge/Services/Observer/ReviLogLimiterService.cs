// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using System.Collections.Concurrent;
using System.Text;
using Revi;

namespace ReviDotNet.Forge.Services.Observer;

public sealed class ReviLogLimiterService : IReviLogLimiter
{
    private readonly ConcurrentDictionary<string, Revi.LogLevel> _map = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _siteSuppress = new(StringComparer.Ordinal);
    private readonly string _path;

    public event Action? Changed;

    public IReadOnlyDictionary<string, Revi.LogLevel> Entries => _map;
    public IReadOnlyDictionary<string, bool> SiteSuppressEntries => _siteSuppress;

    public ReviLogLimiterService()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string? solutionRoot = TryFindSolutionRoot(baseDir);
            _path = solutionRoot is not null
                ? Path.Combine(solutionRoot, "ReviDotNet.Forge", "revilogger_limiter.txt")
                : Path.Combine(baseDir, "revilogger_limiter.txt");
        }
        catch
        {
            _path = Path.Combine(AppContext.BaseDirectory, "revilogger_limiter.txt");
        }

        _ = LoadAsync();
    }

    public bool IsSuppressed(RlogEvent e)
    {
        try
        {
            if (_map.IsEmpty && _siteSuppress.IsEmpty) return false;
            var member = e.Member ?? string.Empty;
            var level = e.Level;

            if (!string.IsNullOrWhiteSpace(e.ClassName) && !string.IsNullOrWhiteSpace(member) && e.Line.HasValue)
            {
                string siteKey = e.ClassName + "." + member + ":" + e.Line.Value;
                if (_siteSuppress.ContainsKey(siteKey)) return true;
            }

            if (!string.IsNullOrWhiteSpace(e.ClassName) && !string.IsNullOrWhiteSpace(member))
            {
                var cm = e.ClassName + "." + member;
                if (_map.TryGetValue(cm, out var minCm)) return level < minCm;
            }

            if (!string.IsNullOrWhiteSpace(e.ClassName) && _map.TryGetValue(e.ClassName, out var minClass))
                return level < minClass;

            if (!string.IsNullOrWhiteSpace(member) && _map.TryGetValue(member, out var min1))
                return level < min1;

            if (!string.IsNullOrWhiteSpace(member))
            {
                string suffix = "." + member;
                foreach (var kv in _map)
                {
                    if (kv.Key.EndsWith(suffix, StringComparison.Ordinal))
                        return level < kv.Value;
                }
            }

            return false;
        }
        catch { return false; }
    }

    public void Set(string key, Revi.LogLevel minLevel)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _map[key.Trim()] = minLevel;
        Changed?.Invoke();
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _map.TryRemove(key.Trim(), out _);
        Changed?.Invoke();
    }

    public void ReplaceAll(IEnumerable<KeyValuePair<string, Revi.LogLevel>> entries)
    {
        _map.Clear();
        _siteSuppress.Clear();
        foreach (var kv in entries)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
                _map[kv.Key.Trim()] = kv.Value;
        }
        Changed?.Invoke();
    }

    public void ReplaceAll(IEnumerable<KeyValuePair<string, Revi.LogLevel>> entries, IEnumerable<string> siteKeys)
    {
        _map.Clear();
        _siteSuppress.Clear();
        foreach (var kv in entries)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
                _map[kv.Key.Trim()] = kv.Value;
        }
        foreach (var s in siteKeys)
        {
            if (!string.IsNullOrWhiteSpace(s))
                _siteSuppress[s.Trim()] = true;
        }
        Changed?.Invoke();
    }

    public void SuppressSite(string siteKey)
    {
        if (string.IsNullOrWhiteSpace(siteKey)) return;
        _siteSuppress[siteKey.Trim()] = true;
        Changed?.Invoke();
    }

    public void RemoveSite(string siteKey)
    {
        if (string.IsNullOrWhiteSpace(siteKey)) return;
        _siteSuppress.TryRemove(siteKey.Trim(), out _);
        Changed?.Invoke();
    }

    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_path)) return false;
            var lines = await File.ReadAllLinesAsync(_path, ct);
            var (dict, sites) = ParseLines(lines);
            ReplaceAll(dict);
            foreach (var s in sites) _siteSuppress[s] = true;
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ReviLogger console limiter configuration");
            sb.AppendLine("# Formats: ClassName.MethodName=LogLevel  OR  ClassName.MethodName:Line (exact suppression)");
            foreach (var kv in _map.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value.ToString().ToLowerInvariant()).AppendLine();
            foreach (var key in _siteSuppress.Keys.OrderBy(k => k, StringComparer.Ordinal))
                sb.AppendLine(key);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path, sb.ToString(), ct);
            return true;
        }
        catch { return false; }
    }

    private static (IEnumerable<KeyValuePair<string, Revi.LogLevel>> entries, IEnumerable<string> siteSuppress) ParseLines(IEnumerable<string> lines)
    {
        var list = new List<KeyValuePair<string, Revi.LogLevel>>();
        var sites = new List<string>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx > 0 && idx < line.Length - 1)
            {
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(key) && TryParseLevel(val, out var level))
                    list.Add(new KeyValuePair<string, Revi.LogLevel>(key, level));
            }
            else if (line.Contains('.') && line.Contains(':'))
            {
                sites.Add(line);
            }
        }
        return (list, sites);
    }

    private static bool TryParseLevel(string s, out Revi.LogLevel level) =>
        Enum.TryParse<Revi.LogLevel>(s, true, out level);

    private static string? TryFindSolutionRoot(string startDir)
    {
        try
        {
            var dir = new DirectoryInfo(startDir);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "BetterNamer.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }
}
