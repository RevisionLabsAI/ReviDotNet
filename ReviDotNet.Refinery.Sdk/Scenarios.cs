// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>A single evaluation case for an agent: seeded inputs plus the rubric/invariants to judge it by.</summary>
public sealed record Scenario
{
    /// <summary>Stable id, unique within a suite.</summary>
    public required string Id { get; init; }

    /// <summary>The agent this scenario exercises.</summary>
    public required string AgentName { get; init; }

    /// <summary>Named inputs passed to the agent run (e.g. <c>issueContext</c>, <c>userMessage</c>).</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>Optional opaque seed for the plugin's isolated test store (interpreted by the plugin).</summary>
    public string? WorldSeed { get; init; }

    /// <summary>Quality rubric facet names the judge should score (e.g. Groundedness, Neutrality).</summary>
    public IReadOnlyList<string> Rubric { get; init; } = [];

    /// <summary>Ids of invariants expected to hold for this scenario (subset of the plugin's checkers).</summary>
    public IReadOnlyList<string> ExpectedInvariants { get; init; } = [];

    /// <summary>When true, this scenario is held out of proposal generation and used only for validation.</summary>
    public bool HeldOut { get; init; }

    /// <summary>Freeform tags for grouping/filtering.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Human notes describing the intent of the scenario.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// The known-correct answer for this scenario, when one exists (e.g. the expected fact-checker winner).
    /// Used by calibration analysis to judge whether a run's determination was correct. Null when the
    /// scenario has no objective ground truth.
    /// </summary>
    public string? GroundTruth { get; init; }
}

/// <summary>
/// Optional seeding hook a plugin MAY implement <b>in addition to</b> <see cref="IRefinementPlugin"/>.
/// The engine detects it at runtime via an <c>is</c> check, so plugins that need no isolated store
/// (e.g. the chatbot) simply do not implement it.
/// <para>
/// <see cref="ResetAsync"/> is called ONCE before a run begins (clear/initialize the isolated test store).
/// <see cref="SeedAsync"/> is called immediately before EVERY agent sample run (each run may mutate the
/// store), with the <see cref="Scenario"/> about to execute and the plugin's per-campaign DI scope.
/// </para>
/// </summary>
public interface IScenarioWorld
{
    /// <summary>Reset the plugin's isolated test store to a clean state. Called once before a run.</summary>
    Task ResetAsync(IServiceProvider pluginServices, CancellationToken ct = default);

    /// <summary>Seed the store for a specific scenario. Called before each sample run of that scenario.</summary>
    Task SeedAsync(Scenario scenario, IServiceProvider pluginServices, CancellationToken ct = default);
}

/// <summary>A named set of scenarios for one agent.</summary>
public sealed record ScenarioSuite
{
    /// <summary>Stable suite name, e.g. "chatbot-core".</summary>
    public required string Name { get; init; }

    /// <summary>The agent these scenarios exercise.</summary>
    public required string AgentName { get; init; }

    /// <summary>The scenarios in this suite.</summary>
    public IReadOnlyList<Scenario> Scenarios { get; init; } = [];
}
