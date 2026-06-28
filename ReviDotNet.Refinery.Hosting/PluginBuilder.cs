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
    public async Task<BuildResult> BuildAsync(string csprojPath, string configuration, CancellationToken ct = default)
    {
        if (!File.Exists(csprojPath))
            return BuildResult.Fail($"project not found: {csprojPath}");

        string dir = Path.GetDirectoryName(csprojPath) ?? Directory.GetCurrentDirectory();

        (int code, string outp, string err) = await RunAsync(
            ["build", csprojPath, "-c", configuration, "--nologo", "-clp:ErrorsOnly"], dir, ct);
        if (code != 0)
            return BuildResult.Fail($"dotnet build failed (exit {code}):\n{Tail(outp + "\n" + err)}");

        (int pc, string pout, string perr) = await RunAsync(
            ["msbuild", csprojPath, "-getProperty:TargetPath", "-nologo"], dir, ct);
        if (pc != 0)
            return BuildResult.Fail($"could not resolve TargetPath (exit {pc}):\n{Tail(pout + "\n" + perr)}");

        string? target = pout.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
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
