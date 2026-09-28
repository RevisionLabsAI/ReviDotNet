using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Revi;

/// <summary>Dependency-free validation of the decision file's standard RConfig envelope, shared by runtime and analyzers.</summary>
internal static class DecisionFileHeader
{
    /// <summary>Validates metadata and section boundaries; the raw decision body's YAML is validated separately.</summary>
    internal static Dictionary<string, string> ReadMetadata(string text)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        HashSet<string> sections = new(StringComparer.Ordinal);
        string section = "";
        using StringReader reader = new(text);
        string? line;
        int number = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            number++;
            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                section = line.Substring(2, line.Length - 4).Trim();
                if (section is not ("information" or "settings" or "_decision"))
                    throw new FormatException($"Unknown decision RConfig section at line {number}.");
                if (!sections.Add(section)) throw new FormatException($"Duplicate decision RConfig section at line {number}.");
                continue;
            }
            // Raw content cannot supply or override metadata, even if it contains name/model text.
            if (section == "_decision") continue;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            int separator = line.IndexOf('=');
            if (separator < 0) throw new FormatException($"Expected RConfig key = value at line {number}; place YAML inside [[_decision]].");
            string key = section + "_" + line.Substring(0, separator).Trim();
            if (key is not ("information_name" or "information_version" or "settings_model"))
                throw new FormatException($"Unknown decision RConfig key at line {number}.");
            if (values.ContainsKey(key)) throw new FormatException($"Duplicate decision RConfig key at line {number}.");
            values.Add(key, line.Substring(separator + 1).Trim());
        }
        if (!sections.Contains("information") || !sections.Contains("settings") || !sections.Contains("_decision"))
            throw new FormatException("Decision files require [[information]], [[settings]], and [[_decision]] sections.");
        foreach (string key in new[] { "information_name", "settings_model" })
            if (!values.TryGetValue(key, out string? value) || !Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._/-]*\z"))
                throw new FormatException("Decision name and model must be unquoted, single-line identifiers.");
        if (!values.TryGetValue("information_version", out string? version) ||
            !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
            throw new FormatException("Decision information.version must be a positive integer.");
        return values;
    }
}
