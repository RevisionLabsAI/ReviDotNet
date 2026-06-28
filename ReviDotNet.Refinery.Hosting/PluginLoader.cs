// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using System.Runtime.Loader;

namespace Revi.Refinery.Hosting;

/// <summary>Loads a built plugin assembly into a collectible context and instantiates its
/// <see cref="IRefinementPlugin"/>, warning on ReviDotNet version skew vs the host.</summary>
public sealed class PluginLoader
{
    /// <summary>Load and instantiate the plugin from a built assembly path.</summary>
    public LoadResult Load(string assemblyPath)
    {
        PluginLoadContext? ctx = null;
        try
        {
            ctx = new PluginLoadContext(assemblyPath);
            Assembly asm = ctx.LoadFromAssemblyPath(assemblyPath);

            Type? pluginType = asm.GetTypes().FirstOrDefault(t =>
                typeof(IRefinementPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
            if (pluginType is null)
                return LoadResult.Fail(ctx, $"no IRefinementPlugin implementation found in {Path.GetFileName(assemblyPath)}");

            if (Activator.CreateInstance(pluginType) is not IRefinementPlugin plugin)
                return LoadResult.Fail(ctx, $"could not instantiate {pluginType.FullName}");

            return new LoadResult(true, plugin, ctx, null, CheckVersionSkew(asm));
        }
        catch (ReflectionTypeLoadException rtl)
        {
            string detail = string.Join("; ", rtl.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct());
            return LoadResult.Fail(ctx, $"type load error: {detail}");
        }
        catch (Exception ex)
        {
            return LoadResult.Fail(ctx, ex.Message);
        }
    }

    /// <summary>Warn if the plugin references a different ReviDotNet.Core/.Sdk version than the host runs.</summary>
    private static string? CheckVersionSkew(Assembly pluginAsm)
    {
        Version? hostCore = typeof(IBuiltInTool).Assembly.GetName().Version;
        Version? hostSdk = typeof(IRefinementPlugin).Assembly.GetName().Version;
        List<string> warns = [];
        foreach (AssemblyName r in pluginAsm.GetReferencedAssemblies())
        {
            if (r.Name == "ReviDotNet.Core" && r.Version is not null && r.Version != hostCore)
                warns.Add($"built against ReviDotNet.Core {r.Version}, host runs {hostCore}");
            if (r.Name == "ReviDotNet.Refinery.Sdk" && r.Version is not null && r.Version != hostSdk)
                warns.Add($"built against ReviDotNet.Refinery.Sdk {r.Version}, host runs {hostSdk}");
        }
        return warns.Count > 0 ? string.Join("; ", warns) : null;
    }
}

/// <summary>The result of loading a plugin assembly.</summary>
/// <param name="Success">Whether a plugin was loaded.</param>
/// <param name="Plugin">The instantiated plugin on success.</param>
/// <param name="Context">The collectible load context (for unload/reload).</param>
/// <param name="Error">Error text on failure.</param>
/// <param name="Warning">A non-fatal warning (e.g. version skew).</param>
public sealed record LoadResult(
    bool Success,
    IRefinementPlugin? Plugin,
    AssemblyLoadContext? Context,
    string? Error,
    string? Warning = null)
{
    public static LoadResult Fail(AssemblyLoadContext? ctx, string error) => new(false, null, ctx, error);
}
