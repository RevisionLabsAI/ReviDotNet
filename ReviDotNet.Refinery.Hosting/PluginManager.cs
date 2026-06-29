// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Revi.Refinery.Hosting;

/// <summary>
/// Discovers, builds, loads, and reloads refinement plugins from the configured local repos, and holds the
/// catalog the dashboard/CLI browse. Registered as a singleton.
/// <para>
/// Concurrency model:
/// <list type="bullet">
/// <item><c>_gate</c> serializes all build/load/unload mutations.</item>
/// <item>The catalog holds IMMUTABLE <see cref="LoadedPlugin"/> records (F12); each state change builds a new
/// record and atomically swaps it under <c>lock(_catalog)</c>, so readers never see a torn instance.</item>
/// <item>Leases (F9): callers about to USE a plugin call <see cref="Acquire"/> to pin it; teardown
/// (<see cref="UnloadInternalAsync"/> via reload/refresh/watch) waits for zero active leases before unloading the
/// ALC, preventing use-after-unload.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PluginManager : IDisposable
{
    private readonly RefineryHostingOptions _options;
    private readonly PluginBuilder _builder;
    private readonly PluginLoader _loader;
    private readonly ILogger<PluginManager>? _log;
    private readonly List<LoadedPlugin> _catalog = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    // F9 lease bookkeeping. Kept on the manager (NOT on the now-immutable LoadedPlugin). Keyed by plugin
    // display name (case-insensitive) since that is what callers Acquire by. `_unloading` names a plugin whose
    // ALC is being torn down: once teardown has observed zero leases it refuses NEW Acquire calls for that name
    // (they get a no-op lease) so a late caller can't pin an assembly that is about to be unloaded — closing the
    // window between the drain's final zero-lease observation and the actual Unload(). All three are guarded by
    // _leaseLock.
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unloading = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _leaseLock = new();

    // Phase 6 file-watch state. One watcher per repo path; a single shared debounce timer per affected
    // project path. Guarded by _watchLock. Disposed in Dispose().
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watchLock = new();
    private bool _disposed;

    public PluginManager(
        IOptions<RefineryHostingOptions> options,
        PluginBuilder builder,
        PluginLoader loader,
        ILogger<PluginManager>? log = null)
    {
        _options = options.Value;
        _builder = builder;
        _loader = loader;
        _log = log;
    }

    /// <summary>A snapshot of the current plugin catalog.</summary>
    public IReadOnlyList<LoadedPlugin> Catalog
    {
        get { lock (_catalog) return _catalog.ToList(); }
    }

    /// <summary>Find a loaded plugin by its display name.</summary>
    public LoadedPlugin? Get(string name) =>
        Catalog.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pin the named plugin against unload/reload for the lifetime of the returned lease (F9). The Forge
    /// agent wraps each campaign run in <c>using (manager.Acquire(name)) { ... }</c>. While at least one lease
    /// is held, <see cref="UnloadInternalAsync"/> (and therefore <see cref="ReloadAsync"/>/<see cref="RefreshAllAsync"/>
    /// and the file watcher) will WAIT before tearing down the plugin's ALC. Returns a no-op disposable when the
    /// plugin is absent. Disposing the lease is idempotent.
    /// </summary>
    public IDisposable Acquire(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Get(name) is null)
            return NoopLease.Instance;

        lock (_leaseLock)
        {
            // Refuse to pin a plugin whose ALC teardown has already committed (zero leases observed). The
            // caller gets a no-op lease; in practice this only happens for a file-watch reload racing a brand
            // new run, and the run will simply re-resolve the reloaded plugin on its next attempt.
            if (_unloading.Contains(name))
                return NoopLease.Instance;
            _leases[name] = (_leases.TryGetValue(name, out int n) ? n : 0) + 1;
        }

        return new Lease(this, name);
    }

    private void Release(string name)
    {
        lock (_leaseLock)
        {
            if (_leases.TryGetValue(name, out int n))
            {
                if (n <= 1) _leases.Remove(name);
                else _leases[name] = n - 1;
            }
        }
    }

    /// <summary>Discover, build, and load all plugins from all configured repos (replacing the catalog).</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await UnloadAllInternalAsync(ct);
            List<LoadedPlugin> fresh = [];
            foreach (RepoSource repo in _options.Repos)
            {
                string config = repo.BuildConfiguration
                    ?? PluginDiscovery.ReadManifestBuildConfig(repo.Path)
                    ?? _options.BuildConfiguration;
                string tfm = repo.TargetFramework ?? _options.TargetFramework;

                foreach (string proj in PluginDiscovery.DiscoverProjects(repo))
                {
                    LoadedPlugin lp = new() { RepoPath = repo.Path, ProjectPath = proj };
                    lp = await BuildAndLoadAsync(lp, config, tfm, ct);
                    fresh.Add(lp);
                }
            }
            lock (_catalog)
            {
                _catalog.Clear();
                _catalog.AddRange(fresh);
            }
        }
        finally { _gate.Release(); }

        if (_options.WatchForChanges)
            SetupWatchers();
    }

    /// <summary>Rebuild + reload a single plugin (by project path or display name).</summary>
    public async Task ReloadAsync(string projectOrName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            LoadedPlugin? lp;
            lock (_catalog)
                lp = _catalog.FirstOrDefault(p =>
                    p.ProjectPath == projectOrName || string.Equals(p.Name, projectOrName, StringComparison.OrdinalIgnoreCase));
            if (lp is null) return;

            await UnloadInternalAsync(lp, ct);
            string config = _options.Repos.FirstOrDefault(r => r.Path == lp.RepoPath)?.BuildConfiguration
                ?? _options.BuildConfiguration;
            string tfm = _options.Repos.FirstOrDefault(r => r.Path == lp.RepoPath)?.TargetFramework
                ?? _options.TargetFramework;

            LoadedPlugin rebuilt = await BuildAndLoadAsync(lp with { Plugin = null, Context = null }, config, tfm, ct);
            Swap(lp, rebuilt);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Build + load <paramref name="seed"/> and return a NEW immutable record reflecting the outcome (F12).
    /// Never mutates the input.
    /// </summary>
    private async Task<LoadedPlugin> BuildAndLoadAsync(LoadedPlugin seed, string configuration, string targetFramework, CancellationToken ct)
    {
        LoadedPlugin building = seed with
        {
            Status = PluginStatus.Building,
            Error = null,
            Warning = null,
            Plugin = null,
            Context = null
        };

        BuildResult build = await _builder.BuildAsync(building.ProjectPath, configuration, targetFramework, ct);
        if (!build.Success)
        {
            _log?.LogWarning("Refinery plugin build failed for {Project}: {Error}", building.ProjectPath, build.Error);
            return building with { Status = PluginStatus.BuildFailed, Error = build.Error };
        }

        LoadResult load = _loader.Load(build.AssemblyPath!);
        if (!load.Success)
        {
            _log?.LogWarning("Refinery plugin load failed for {Project}: {Error}", building.ProjectPath, load.Error);
            return building with
            {
                AssemblyPath = build.AssemblyPath,
                Context = load.Context,
                Status = PluginStatus.LoadFailed,
                Error = load.Error
            };
        }

        LoadedPlugin loaded = building with
        {
            AssemblyPath = build.AssemblyPath,
            Context = load.Context,
            Plugin = load.Plugin,
            Warning = load.Warning,
            Status = PluginStatus.Loaded,
            LoadedAt = DateTime.UtcNow
        };

        if (load.Warning is not null)
            _log?.LogWarning("Refinery plugin {Name} loaded with warning: {Warning}", loaded.Name, load.Warning);
        else
            _log?.LogInformation("Refinery plugin {Name} loaded from {Project}", loaded.Name, loaded.ProjectPath);
        return loaded;
    }

    /// <summary>Atomically replace <paramref name="oldLp"/> with <paramref name="newLp"/> in the catalog.</summary>
    private void Swap(LoadedPlugin oldLp, LoadedPlugin newLp)
    {
        lock (_catalog)
        {
            int idx = _catalog.IndexOf(oldLp);
            if (idx >= 0) _catalog[idx] = newLp;
            else _catalog.Add(newLp);
        }
    }

    private async Task UnloadAllInternalAsync(CancellationToken ct)
    {
        List<LoadedPlugin> snapshot;
        lock (_catalog) snapshot = _catalog.ToList();
        foreach (LoadedPlugin lp in snapshot)
            await UnloadInternalAsync(lp, ct);
    }

    /// <summary>
    /// Tear down a plugin's ALC AFTER all active leases are released (F9). Caller MUST hold <c>_gate</c>, which
    /// serializes teardowns against each other. We poll the lease count (with a short delay rather than blocking
    /// a thread); once we observe zero leases we mark the name in <c>_unloading</c> under the SAME _leaseLock
    /// that <see cref="Acquire"/> uses, so a late <see cref="Acquire"/> can no longer pin the assembly we are
    /// about to unload — it gets a no-op lease instead. The mark is cleared in a finally after Unload().
    /// </summary>
    private async Task UnloadInternalAsync(LoadedPlugin lp, CancellationToken ct)
    {
        string name = lp.Name;

        // Wait for in-flight users (campaign runs) to finish, then atomically claim the unload: the final
        // zero-lease check and the `_unloading` mark happen under the SAME _leaseLock acquisition that Acquire
        // uses, so no new lease can slip in between observing zero and committing the teardown.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_leaseLock)
            {
                if (!_leases.TryGetValue(name, out int n) || n == 0)
                {
                    _unloading.Add(name);
                    break;
                }
            }
            await Task.Delay(25, ct);
        }

        try
        {
            lp.Context?.Unload();
        }
        finally
        {
            lock (_leaseLock)
                _unloading.Remove(name);
        }
    }

    // ---- Phase 6: opt-in file watching -------------------------------------------------------------

    private void SetupWatchers()
    {
        if (_disposed) return;
        lock (_watchLock)
        {
            DisposeWatchersNoLock();

            foreach (RepoSource repo in _options.Repos)
            {
                string root = repo.Path;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;
                try
                {
                    FileSystemWatcher watcher = new(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                    };
                    // Two filters (*.cs and *.csproj) via the Filters collection.
                    watcher.Filters.Add("*.cs");
                    watcher.Filters.Add("*.csproj");

                    FileSystemEventHandler onChanged = (_, e) => OnRepoFileChanged(root, e.FullPath);
                    watcher.Changed += onChanged;
                    watcher.Created += onChanged;
                    watcher.Renamed += (_, e) => OnRepoFileChanged(root, e.FullPath);
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Refinery file-watch setup failed for repo {Repo}", root);
                }
            }
        }
    }

    private void OnRepoFileChanged(string repoRoot, string changedPath)
    {
        if (_disposed) return;

        // Ignore build artifacts.
        if (changedPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            changedPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return;

        // Map the changed file to the affected plugin in this repo (the plugin whose project directory is the
        // longest prefix of the changed path).
        LoadedPlugin? affected = null;
        foreach (LoadedPlugin p in Catalog)
        {
            if (!string.Equals(p.RepoPath, repoRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            string? projDir = Path.GetDirectoryName(p.ProjectPath);
            if (projDir is null) continue;
            if (changedPath.StartsWith(projDir, StringComparison.OrdinalIgnoreCase))
            {
                if (affected is null || projDir.Length > (Path.GetDirectoryName(affected.ProjectPath)?.Length ?? 0))
                    affected = p;
            }
        }
        if (affected is null) return;

        DebounceReload(affected.ProjectPath);
    }

    private void DebounceReload(string projectPath)
    {
        int debounceMs = _options.WatchDebounceMs > 0 ? _options.WatchDebounceMs : 500;
        lock (_watchLock)
        {
            if (_disposed) return;
            if (_debounceTimers.TryGetValue(projectPath, out Timer? existing))
            {
                existing.Change(debounceMs, Timeout.Infinite);
                return;
            }
            Timer timer = new(_ => OnDebounceElapsed(projectPath), null, debounceMs, Timeout.Infinite);
            _debounceTimers[projectPath] = timer;
        }
    }

    private void OnDebounceElapsed(string projectPath)
    {
        lock (_watchLock)
        {
            if (_debounceTimers.Remove(projectPath, out Timer? t))
                t.Dispose();
        }
        if (_disposed) return;

        // Fire-and-forget the reload; ReloadAsync serializes via _gate and waits on leases internally.
        _ = ReloadSafeAsync(projectPath);
    }

    private async Task ReloadSafeAsync(string projectPath)
    {
        try
        {
            await ReloadAsync(projectPath);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Refinery file-watch reload failed for {Project}", projectPath);
        }
    }

    private void DisposeWatchersNoLock()
    {
        foreach (FileSystemWatcher w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* ignore */ }
        }
        _watchers.Clear();
        foreach (Timer t in _debounceTimers.Values)
        {
            try { t.Dispose(); } catch { /* ignore */ }
        }
        _debounceTimers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_watchLock)
            DisposeWatchersNoLock();
        _gate.Dispose();
    }

    /// <summary>A lease that decrements the plugin's pin count once (idempotent) on dispose.</summary>
    private sealed class Lease(PluginManager owner, string name) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(name);
        }
    }

    /// <summary>A no-op lease returned when the requested plugin is absent.</summary>
    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
}
