// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Revi;

public static class NodeIdentity
{
    /// <summary>
    /// Returns a stable machine identifier if possible, with overrides and file persistence fallback.
    /// </summary>
    public static string GetMachineId(string? forced = null, string? appName = null, bool machineWide = true)
    {
        if (!string.IsNullOrWhiteSpace(forced))
            return forced.Trim();

        var env = Environment.GetEnvironmentVariable("REVILOGGER_MACHINE_ID");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        var osId = TryGetOsStableMachineId();
        if (!string.IsNullOrWhiteSpace(osId))
            return osId!;

        string app = string.IsNullOrWhiteSpace(appName) ? "ReviDotNet" : appName!;
        string root = machineWide
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(root, app);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "machine-id");

        if (File.Exists(path))
        {
            var id = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(id)) return id;
        }

        var newId = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, newId);
        return newId;
    }

    /// <summary>
    /// Generates a unique instance identifier for this process and returns the instance start time.
    /// </summary>
    public static string InstanceIdUtc(out DateTimeOffset startedUtc, bool includePid = true)
    {
        startedUtc = DateTimeOffset.UtcNow;
        var guid = Guid.NewGuid().ToString("N");
        if (includePid)
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                return $"{guid}@{startedUtc:O}#pid-{proc.Id}";
            }
            catch { /* ignore */ }
        }
        return $"{guid}@{startedUtc:O}";
    }

    private static string? TryGetOsStableMachineId()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    "SOFTWARE\\Microsoft\\Cryptography", false);
                var v = key?.GetValue("MachineGuid") as string;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            const string path = "/etc/machine-id";
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch { return null; }
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/ioreg",
                    ArgumentList = { "-rd1", "-c", "IOPlatformExpertDevice" },
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(startInfo)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                var key = "IOPlatformUUID\" = \"";
                var idx = output.IndexOf(key, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    idx += key.Length;
                    var end = output.IndexOf('"', idx);
                    if (end > idx)
                        return output.Substring(idx, end - idx).Trim();
                }
            }
            catch { }
            return null;
        }
        return null;
    }
}