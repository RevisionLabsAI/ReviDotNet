// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;

namespace Revi.Refinery.Hosting;

/// <summary>Builds a plugin project via the <c>dotnet</c> CLI (incremental — a no-op when current) and
/// resolves its output assembly path.</summary>
public sealed class PluginBuilder
{
    /// <summary>Build the project and return its output assembly path, or an error.</summary>
    /// <param name="csprojPath">The plugin project file.</param>
    /// <param name="configuration">Build configuration (Debug/Release).</param>
    /// <param name="targetFramework">
    /// Target framework moniker to build/resolve (F11). Passed as <c>-p:TargetFramework={tfm}</c> to both the
    /// build and the TargetPath query so they agree even when the project sets <c>&lt;TargetFrameworks&gt;</c>
    /// (plural). When null/blank, no TFM is forced (single-TFM projects).
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<BuildResult> BuildAsync(string csprojPath, string configuration, string? targetFramework = null, CancellationToken ct = default)
    {
        if (!File.Exists(csprojPath))
            return BuildResult.Fail($"project not found: {csprojPath}");

        string dir = Path.GetDirectoryName(csprojPath) ?? Directory.GetCurrentDirectory();
        bool haveTfm = !string.IsNullOrWhiteSpace(targetFramework);

        List<string> buildArgs = ["build", csprojPath, "-c", configuration, "--nologo", "-clp:ErrorsOnly"];
        if (haveTfm) buildArgs.Add($"-p:TargetFramework={targetFramework}");
        (int code, string outp, string err) = await RunAsync(buildArgs.ToArray(), dir, ct);
        if (code != 0)
            return BuildResult.Fail($"dotnet build failed (exit {code}):\n{Tail(outp + "\n" + err)}");

        List<string> propArgs = ["msbuild", csprojPath, "-getProperty:TargetPath", "-nologo"];
        if (haveTfm) propArgs.Add($"-p:TargetFramework={targetFramework}");
        (int pc, string pout, string perr) = await RunAsync(propArgs.ToArray(), dir, ct);
        if (pc != 0)
            return BuildResult.Fail($"could not resolve TargetPath (exit {pc}):\n{Tail(pout + "\n" + perr)}");

        // -getProperty:TargetPath should be a single line, but with a multi-TFM project that forgot to honor
        // the forced TFM it can return several. Prefer a dll whose path contains the chosen TFM folder; else
        // fall back to the last dll line (the legacy behaviour).
        List<string> dlls = pout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? target = null;
        if (haveTfm)
        {
            string tfmSegment = $"{Path.DirectorySeparatorChar}{targetFramework}{Path.DirectorySeparatorChar}";
            string tfmSegmentAlt = $"/{targetFramework}/";
            target = dlls.LastOrDefault(d =>
                d.Contains(tfmSegment, StringComparison.OrdinalIgnoreCase) ||
                d.Contains(tfmSegmentAlt, StringComparison.OrdinalIgnoreCase));
        }
        target ??= dlls.LastOrDefault();

        if (string.IsNullOrEmpty(target) || !File.Exists(target))
            return BuildResult.Fail($"TargetPath did not resolve to an existing dll (got '{target}')");

        return BuildResult.Ok(target);
    }

    private static async Task<(int code, string stdout, string stderr)> RunAsync(string[] args, string workingDir, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process proc = new() { StartInfo = psi };
        proc.Start();
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await outTask, await errTask);
    }

    private static string Tail(string s)
    {
        string[] lines = s.Split('\n');
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 30)));
    }
}

/// <summary>The result of building a plugin project.</summary>
/// <param name="Success">Whether the build succeeded and an assembly was resolved.</param>
/// <param name="AssemblyPath">The output assembly path on success.</param>
/// <param name="Error">The error text on failure.</param>
public sealed record BuildResult(bool Success, string? AssemblyPath, string? Error)
{
    public static BuildResult Ok(string path) => new(true, path, null);
    public static BuildResult Fail(string error) => new(false, null, error);
}
