using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Revi;

/// <summary>A versioned, human-authored decision question pack.</summary>
public sealed record DecisionPrompt(string Name, int Version, string Model, IReadOnlyDictionary<string, DecisionQuestion> Questions)
{
    /// <summary>Original file, when loaded from disk; used by editors without reconstructing prose.</summary>
    public string? SourcePath { get; init; }
    /// <summary>Original authored source, including comments, for text editors.</summary>
    public string? SourceText { get; init; }
    /// <summary>Deterministic identity for effective questions, including their descriptions and ordering.</summary>
    public string Hash => ComputeHash(Questions);
    /// <summary>Hashes questions without state, provider credentials, or policy thresholds.</summary>
    public static string ComputeHash(IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        string content = JsonSerializer.Serialize(questions.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new { id = p.Key, type = p.Value.Kind, instructions = p.Value.Instructions, criteria = p.Value.Criteria }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}

/// <summary>Reads standard RConfig .decision files with YAML only inside the raw [[_decision]] section.</summary>
public static class DecisionPromptParser
{
    /// <summary>Parses one question pack; rejects duplicate/unknown fields, aliases and custom tags.</summary>
    public static DecisionPrompt Parse(string text)
    {
        if (text.Length > 1_000_000) throw new FormatException("Decision file exceeds one million characters.");
        DecisionFileHeader.ReadMetadata(text);
        Dictionary<string, string> config = RConfigParser.ReadEmbedded(text, preserveRawSectionWhitespace: true);
        string name = config["information_name"];
        int version = int.Parse(config["information_version"], System.Globalization.CultureInfo.InvariantCulture);
        string model = config["settings_model"];
        if (!config.TryGetValue("_decision", out string? decision) || string.IsNullOrWhiteSpace(decision))
            throw new FormatException("The [[_decision]] section requires a YAML decision object.");
        YamlStream stream = new();
        try { stream.Load(new StringReader(decision)); }
        catch (YamlDotNet.Core.YamlException ex) { throw new FormatException($"Invalid YAML in [[_decision]] at block line {ex.Start.Line}, column {ex.Start.Column}."); }
        if (stream.Documents.Count != 1) throw new FormatException("Exactly one YAML document is required.");
        Dictionary<string, object?> root = Map(ConvertNode(stream.Documents[0].RootNode, 0), "decision");
        Fields(root, "questions");
        Dictionary<string, object?> rawQuestions = Map(root.GetValueOrDefault("questions"), "questions");
        Dictionary<string, DecisionQuestion> questions = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in rawQuestions)
        {
            Dictionary<string, object?> q = Map(pair.Value, pair.Key);
            Fields(q, "type", "instructions", "criteria");
            object instructions = q.GetValueOrDefault("instructions") ?? throw new FormatException($"Question '{pair.Key}' needs instructions.");
            object? criteria = q.GetValueOrDefault("criteria");
            questions.Add(pair.Key, Text(q, "type") switch
            {
                "choice" => new ChoiceQuestion(instructions, Map(criteria, "criteria")),
                "boolean" or "noul" => new BooleanQuestion(instructions, criteria is null ? null : Map(criteria, "criteria")),
                "score" => new ScoreQuestion(instructions, criteria is List<object?> levels && levels.All(l => l is not null)
                    ? levels.Cast<object>().ToArray() : throw new FormatException("Score criteria must be an ordered list.")),
                _ => throw new FormatException($"Question '{pair.Key}' has an unknown type.")
            });
        }
        try { DecisionValidation.Validate(new DecisionRequest(model, "validation", questions)); }
        catch (ArgumentException ex) { throw new FormatException(ex.Message); }
        return new DecisionPrompt(ConfigName.Resolve(name), version, ConfigName.Resolve(model), questions) { SourceText = text };
    }

    /// <summary>Converts only safe YAML data; plain scalars remain strings to preserve labels like yes/false/001.</summary>
    private static object? ConvertNode(YamlNode node, int depth)
    {
        if (depth > 32 || !node.Anchor.IsEmpty || !node.Tag.IsEmpty) throw new FormatException("Decision YAML forbids anchors, aliases, custom tags, and nesting beyond 32 levels.");
        if (node is YamlScalarNode scalar)
            return scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && scalar.Value is ("null" or "~") ? null : scalar.Value ?? "";
        if (node is YamlSequenceNode sequence) return sequence.Children.Select(n => ConvertNode(n, depth + 1)).ToList();
        if (node is YamlMappingNode mapping)
        {
            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
            {
                if (entry.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value) || !result.TryAdd(key.Value, ConvertNode(entry.Value, depth + 1)))
                    throw new FormatException("Decision YAML requires unique, nonempty scalar keys.");
            }
            return result;
        }
        throw new FormatException("Unsupported decision YAML node.");
    }
    /// <summary>Requires a mapping node.</summary>
    private static Dictionary<string, object?> Map(object? value, string field) => value as Dictionary<string, object?> ?? throw new FormatException($"'{field}' must be a mapping.");
    /// <summary>Requires nonempty prose.</summary>
    private static string Text(Dictionary<string, object?> map, string field) => map.GetValueOrDefault(field) is string value && !string.IsNullOrWhiteSpace(value) ? value : throw new FormatException($"'{field}' must be nonempty text.");
    /// <summary>Rejects misspelled schema fields.</summary>
    private static void Fields(Dictionary<string, object?> map, params string[] allowed)
    {
        foreach (string key in map.Keys)
            if (!allowed.Contains(key, StringComparer.Ordinal)) throw new FormatException($"Unknown decision field '{key}'.");
    }
}
