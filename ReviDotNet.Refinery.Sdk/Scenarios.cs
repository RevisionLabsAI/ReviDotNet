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
