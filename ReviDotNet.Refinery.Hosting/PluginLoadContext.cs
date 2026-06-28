// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using System.Runtime.Loader;

namespace Revi.Refinery.Hosting;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> for a single plugin. Plugin-private dependencies load
/// here (isolated), but the shared contract/runtime assemblies (<c>ReviDotNet.Core</c>,
/// <c>ReviDotNet.Refinery.Sdk</c>, the framework, and the DI/Hosting glue) defer to the default context so
/// the plugin's <c>IRefinementPlugin</c> has the same type identity as the host's.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base(name: $"refinery-plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName.Name))
            return null; // -> default ALC (single shared identity)

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }

    /// <summary>Assemblies that must be shared with the host (so boundary types have one identity).</summary>
    internal static bool IsShared(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name is "ReviDotNet.Core" or "ReviDotNet.Refinery.Sdk" or "System" or "netstandard" or "mscorlib"
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Configuration", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Options", StringComparison.Ordinal);
    }
}
