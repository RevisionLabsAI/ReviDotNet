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
/// </summary>
public sealed class PluginManager(
    IOptions<RefineryHostingOptions> options,
    PluginBuilder builder,
    PluginLoader loader,
    ILogger<PluginManager>? log = null)
{
    private readonly RefineryHostingOptions _options = options.Value;
    private readonly PluginBuilder _builder = builder;
    private readonly PluginLoader _loader = loader;
    private readonly ILogger<PluginManager>? _log = log;
    private readonly List<LoadedPlugin> _catalog = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A snapshot of the current plugin catalog.</summary>
    public IReadOnlyList<LoadedPlugin> Catalog
    {
        get { lock (_catalog) return _catalog.ToList(); }
    }

    /// <summary>Find a loaded plugin by its display name.</summary>
    public LoadedPlugin? Get(string name) =>
        Catalog.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Discover, build, and load all plugins from all configured repos (replacing the catalog).</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            UnloadAllInternal();
            List<LoadedPlugin> fresh = [];
            foreach (RepoSource repo in _options.Repos)
            {
                string config = repo.BuildConfiguration
                    ?? PluginDiscovery.ReadManifestBuildConfig(repo.Path)
                    ?? _options.BuildConfiguration;

                foreach (string proj in PluginDiscovery.DiscoverProjects(repo))
                {
                    LoadedPlugin lp = new() { RepoPath = repo.Path, ProjectPath = proj };
                    await BuildAndLoadAsync(lp, config, ct);
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

            UnloadInternal(lp);
            string config = _options.Repos.FirstOrDefault(r => r.Path == lp.RepoPath)?.BuildConfiguration
                ?? _options.BuildConfiguration;
            await BuildAndLoadAsync(lp, config, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task BuildAndLoadAsync(LoadedPlugin lp, string configuration, CancellationToken ct)
    {
        lp.Status = PluginStatus.Building;
        lp.Error = null;
        lp.Warning = null;

        BuildResult build = await _builder.BuildAsync(lp.ProjectPath, configuration, ct);
        if (!build.Success)
        {
            lp.Status = PluginStatus.BuildFailed;
            lp.Error = build.Error;
            _log?.LogWarning("Refinery plugin build failed for {Project}: {Error}", lp.ProjectPath, build.Error);
            return;
        }
        lp.AssemblyPath = build.AssemblyPath;

        LoadResult load = _loader.Load(build.AssemblyPath!);
        lp.Context = load.Context;
        if (!load.Success)
        {
            lp.Status = PluginStatus.LoadFailed;
            lp.Error = load.Error;
            _log?.LogWarning("Refinery plugin load failed for {Project}: {Error}", lp.ProjectPath, load.Error);
            return;
        }

        lp.Plugin = load.Plugin;
        lp.Warning = load.Warning;
        lp.Status = PluginStatus.Loaded;
        lp.LoadedAt = DateTime.UtcNow;
        if (load.Warning is not null)
            _log?.LogWarning("Refinery plugin {Name} loaded with warning: {Warning}", lp.Name, load.Warning);
        else
            _log?.LogInformation("Refinery plugin {Name} loaded from {Project}", lp.Name, lp.ProjectPath);
    }

    private void UnloadAllInternal()
    {
        lock (_catalog)
            foreach (LoadedPlugin lp in _catalog)
                UnloadInternal(lp);
    }

    private static void UnloadInternal(LoadedPlugin lp)
    {
        lp.Plugin = null;
        lp.Context?.Unload();
        lp.Context = null;
        lp.Status = PluginStatus.Discovered;
    }
}
