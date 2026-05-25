// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using System.Text;
using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Reflection-driven serializer that turns a profile object (ModelProfile, ProviderProfile, etc.)
/// back into .rcfg text using the [RConfigProperty] attribute on each property.
/// Properties whose key starts with an underscore (raw sections like _system, _instruction)
/// are emitted as bare sections; the rest are grouped by their section prefix (before the
/// first underscore in the RConfigProperty name).
///
/// Null and empty-string values are omitted so we don't write meaningless defaults.
/// </summary>
public static class RConfigSerializer
{
    public static string Serialize(object profile)
    {
        var props = profile.GetType().GetProperties();
        // section -> list of (key, value) — preserves first-seen order for deterministic output
        var sections = new Dictionary<string, List<(string Key, string Value)>>();
        var sectionOrder = new List<string>();
        var rawSections = new Dictionary<string, string>();
        var rawOrder = new List<string>();

        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<RConfigPropertyAttribute>();
            if (attr is null) continue;

            object? raw = prop.GetValue(profile);
            if (raw is null) continue;

            string asString = FormatValue(raw);
            if (string.IsNullOrEmpty(asString)) continue;

            string attrName = attr.Name;

            // Raw section: name starts with "_" (e.g. _system, _instruction).
            if (attrName.StartsWith("_"))
            {
                string sectionName = attrName; // raw section header is the full key
                if (!rawSections.ContainsKey(sectionName))
                    rawOrder.Add(sectionName);
                rawSections[sectionName] = asString;
                continue;
            }

            // Structured section: "<section>_<field>" e.g. "general_name" -> [[general]] name = ...
            int underscore = attrName.IndexOf('_');
            string section = underscore > 0 ? attrName[..underscore] : "general";
            string field = underscore > 0 ? attrName[(underscore + 1)..] : attrName;

            if (!sections.ContainsKey(section))
            {
                sections[section] = new();
                sectionOrder.Add(section);
            }
            sections[section].Add((field, asString));
        }

        var sb = new StringBuilder();
        foreach (var section in sectionOrder)
        {
            sb.AppendLine($"[[{section}]]");
            foreach (var (key, value) in sections[section])
                sb.AppendLine($"{key} = {value}");
            sb.AppendLine();
        }
        foreach (var section in rawOrder)
        {
            sb.AppendLine($"[[{section}]]");
            sb.AppendLine(rawSections[section].Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatValue(object value)
    {
        switch (value)
        {
            case string s: return s;
            case bool b: return b ? "true" : "false";
            case Enum e: return e.ToString();
            case IEnumerable<string> list: return string.Join(",", list);
            default: return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
