// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using Newtonsoft.Json;

namespace Revi.Refinery;

/// <summary>Generates new evaluation scenarios for an agent via an LLM test-author prompt.</summary>
public interface IScenarioGenerator
{
    /// <summary>
    /// Propose up to <paramref name="count"/> new scenarios for <paramref name="agentName"/> in
    /// <paramref name="targetCategory"/>, deduplicated against <paramref name="existing"/> (and against each
    /// other) by a normalized fingerprint. Returns an empty list if nothing useful could be generated.
    /// </summary>
    Task<IReadOnlyList<Scenario>> GenerateAsync(
        string agentName,
        string agentSpecSection,
        IReadOnlyList<Scenario> existing,
        string targetCategory,
        int count = 5,
        CancellationToken ct = default);
}

/// <summary>
/// Drives the <c>Evaluator.ScenarioGenerator</c> prompt (pinned to a high-effort reasoning model) to author
/// fresh, diverse evaluation scenarios that probe an agent's spec — including a verifiable ground truth.
/// Candidates are deduped against the existing suite (and within the batch) by a normalized fingerprint of
/// (agent name + sorted tags + sorted input values) so the suite does not accumulate near-duplicates.
/// </summary>
public sealed class ScenarioGenerator(IInferService infer) : IScenarioGenerator
{
    /// <summary>The scenario-generator prompt name (shipped embedded in this assembly's RConfigs).</summary>
    public const string GeneratorPromptName = "Evaluator.ScenarioGenerator";

    private readonly IInferService _infer = infer;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Scenario>> GenerateAsync(
        string agentName,
        string agentSpecSection,
        IReadOnlyList<Scenario> existing,
        string targetCategory,
        int count = 5,
        CancellationToken ct = default)
    {
        if (count <= 0)
            return [];

        List<Input> inputs =
        [
            new("Agent Name", agentName),
            new("Agent Spec Section", agentSpecSection),
            new("Existing Scenarios", RenderExisting(existing)),
            new("Target Category", targetCategory),
            new("Count", count.ToString())
        ];

        ScenarioGenResponse? resp = await _infer.ToObject<ScenarioGenResponse>(GeneratorPromptName, inputs, token: ct);
        if (resp?.Scenarios is not { Count: > 0 })
            return [];

        // Seed the dedup set with every existing scenario's fingerprint.
        HashSet<string> seen = [];
        foreach (Scenario s in existing)
            seen.Add(Fingerprint(s.AgentName, s.Tags, s.Inputs.Values));

        List<Scenario> result = [];
        foreach (ScenarioGenItem item in resp.Scenarios)
        {
            if (result.Count >= count)
                break;

            Dictionary<string, string> mappedInputs = MapInputs(item.Inputs);
            IReadOnlyList<string> tags = item.Tags is { Count: > 0 } ? item.Tags : [];

            string fp = Fingerprint(agentName, tags, mappedInputs.Values);
            if (!seen.Add(fp))
                continue; // duplicate of an existing scenario or an earlier candidate in this batch

            result.Add(new Scenario
            {
                Id = !string.IsNullOrWhiteSpace(item.Id) ? item.Id! : $"gen-{Guid.NewGuid():N}"[..12],
                AgentName = agentName,
                Inputs = mappedInputs,
                WorldSeed = string.IsNullOrWhiteSpace(item.WorldSeed) ? null : item.WorldSeed,
                Rubric = item.Rubric is { Count: > 0 } ? item.Rubric : [],
                ExpectedInvariants = item.ExpectedInvariants is { Count: > 0 } ? item.ExpectedInvariants : [],
                HeldOut = item.HeldOut,
                Tags = tags,
                Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes,
                GroundTruth = string.IsNullOrWhiteSpace(item.GroundTruth) ? null : item.GroundTruth
            });
        }

        return result;
    }

    private static Dictionary<string, string> MapInputs(Dictionary<string, string>? inputs)
    {
        if (inputs is not { Count: > 0 })
            return [];

        Dictionary<string, string> map = new(inputs.Count);
        foreach (KeyValuePair<string, string> kv in inputs)
            map[kv.Key] = kv.Value ?? string.Empty;
        return map;
    }

    /// <summary>A compact, token-cheap JSON summary of the existing suite so the model can avoid duplicating it.</summary>
    private static string RenderExisting(IReadOnlyList<Scenario> existing)
    {
        if (existing.Count == 0)
            return "[]";

        var summary = existing.Select(s => new
        {
            id = s.Id,
            tags = s.Tags,
            inputs = s.Inputs.Keys,
            notes = s.Notes
        });
        return JsonConvert.SerializeObject(summary, Formatting.None);
    }

    /// <summary>
    /// Stable, case-insensitive fingerprint of a scenario's identity: agent name + sorted tag set +
    /// sorted input values. Tags and values are normalized (trimmed, lower-cased) so cosmetic differences
    /// (ordering, casing, surrounding whitespace) collapse to the same fingerprint. Keys are intentionally
    /// excluded — two scenarios with the same VALUES under different key names are still duplicates here.
    /// </summary>
    internal static string Fingerprint(string agentName, IEnumerable<string> tags, IEnumerable<string> inputValues)
    {
        string name = Normalize(agentName);

        string tagPart = string.Join(
            ",",
            tags.Select(Normalize).Where(t => t.Length > 0).Distinct().OrderBy(t => t, StringComparer.Ordinal));

        string valuePart = string.Join(
            "",
            inputValues.Select(Normalize).OrderBy(v => v, StringComparer.Ordinal));

        StringBuilder sb = new();
        sb.Append(name).Append("|tags=").Append(tagPart).Append("|vals=").Append(valuePart);
        return sb.ToString();
    }

    private static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? string.Empty
            : string.Join(' ', s.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>DTO matching the <c>Evaluator.ScenarioGenerator</c> prompt's JSON output.</summary>
    private sealed class ScenarioGenResponse
    {
        [JsonProperty("scenarios")] public List<ScenarioGenItem>? Scenarios { get; set; }
    }

    /// <summary>A single generated scenario as returned by the prompt.</summary>
    private sealed class ScenarioGenItem
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("inputs")] public Dictionary<string, string>? Inputs { get; set; }
        [JsonProperty("world_seed")] public string? WorldSeed { get; set; }
        [JsonProperty("rubric")] public List<string>? Rubric { get; set; }
        [JsonProperty("expected_invariants")] public List<string>? ExpectedInvariants { get; set; }
        [JsonProperty("held_out")] public bool HeldOut { get; set; }
        [JsonProperty("tags")] public List<string>? Tags { get; set; }
        [JsonProperty("notes")] public string? Notes { get; set; }
        [JsonProperty("ground_truth")] public string? GroundTruth { get; set; }
    }
}
