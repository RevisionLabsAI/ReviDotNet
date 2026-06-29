// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// A reusable test scenario for the Test Runner page — prompt, inputs, models, runs, analysis toggle.
/// Persisted as JSON under RConfigs/.suites/.
/// </summary>
public sealed class SavedSuite
{
    public string Name { get; set; } = string.Empty;
    public string PromptName { get; set; } = string.Empty;
    public List<string> ModelNames { get; set; } = new();
    public int RunsPerModel { get; set; } = 1;
    public bool RunAnalysis { get; set; } = true;
    public List<SavedSuiteInput> Inputs { get; set; } = new();

    /// <summary>
    /// When set, the suite targets an agent (run via the Agent Workshop) rather than a bare prompt.
    /// Null means prompt-mode (the historical behaviour, using <see cref="PromptName"/>).
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Optional assertions evaluated against each case's output to decide pass/fail.
    /// Null (or empty) means no assertions — a case with none counts as passed.
    /// </summary>
    public List<SuiteAssertion>? Assertions { get; set; }
}

public sealed class SavedSuiteInput
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>The kind of check a <see cref="SuiteAssertion"/> performs against a case's output.</summary>
public enum AssertionKind
{
    /// <summary>Output must contain the target text (case-insensitive substring).</summary>
    Contains,
    /// <summary>Output must NOT contain the target text (case-insensitive substring).</summary>
    NotContains,
    /// <summary>Output must match the target regular expression.</summary>
    Regex,
    /// <summary>A dotted JSON path (e.g. <c>a.b.c</c> or <c>a.0.b</c>) must resolve in the output JSON.</summary>
    JsonPath,
    /// <summary>A judge-scored quality assessment of the output must meet <see cref="SuiteAssertion.Threshold"/>.</summary>
    ScoreMin
}

/// <summary>
/// One assertion in a suite. <see cref="Target"/> is the substring / pattern / json path / rubric the
/// <see cref="AssertionKind"/> interprets; <see cref="Threshold"/> is used only by <see cref="AssertionKind.ScoreMin"/>.
/// </summary>
public sealed record SuiteAssertion(string Id, AssertionKind Kind, string Target, double? Threshold = null);

/// <summary>
/// The outcome of evaluating a single <see cref="SuiteAssertion"/> against an output.
/// </summary>
public sealed record AssertionResult(string Id, bool Passed, string? ActualSnippet, string? FailReason);

/// <summary>
/// File-backed persistence for Test Runner saved suites. Stored as one JSON per suite under
/// RConfigs/.suites/ — keeps the data alongside other RConfig artifacts and makes suites
/// version-controllable.
/// </summary>
public sealed class SavedSuitesService
{
    private readonly string _root;

    public SavedSuitesService(IConfiguration config)
    {
        string promptsPath = config["Forge:PromptsSourcePath"] ?? "RConfigs/Prompts";
        string baseDir = Path.GetDirectoryName(promptsPath) ?? "RConfigs";
        _root = Path.Combine(baseDir, ".suites");
    }

    public List<SavedSuite> ListAll()
    {
        var list = new List<SavedSuite>();
        if (!Directory.Exists(_root)) return list;

        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var suite = JsonConvert.DeserializeObject<SavedSuite>(json);
                if (suite is not null && !string.IsNullOrWhiteSpace(suite.Name))
                    list.Add(suite);
            }
            catch { /* skip malformed */ }
        }

        return list.OrderBy(s => s.Name).ToList();
    }

    public void Save(SavedSuite suite)
    {
        if (string.IsNullOrWhiteSpace(suite.Name))
            throw new ArgumentException("Suite name is required.", nameof(suite));

        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Sanitize(suite.Name) + ".json");
        var json = JsonConvert.SerializeObject(suite, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    public void Delete(string suiteName)
    {
        if (string.IsNullOrWhiteSpace(suiteName)) return;
        string path = Path.Combine(_root, Sanitize(suiteName) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
