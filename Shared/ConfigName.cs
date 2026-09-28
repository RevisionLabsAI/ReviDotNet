using System;

namespace Revi;

/// <summary>Single naming rule shared by runtime registries and compile-time analyzers.</summary>
internal static class ConfigName
{
    /// <summary>Uses the declared name verbatim. Folders and file names do not form part of identity.</summary>
    public static string Resolve(string declaredName)
    {
        if (string.IsNullOrWhiteSpace(declaredName)) throw new ArgumentException("A configuration name is required.", nameof(declaredName));
        return declaredName;
    }
}
