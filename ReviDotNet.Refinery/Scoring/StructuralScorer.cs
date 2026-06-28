// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Runs a scenario's expected <see cref="IInvariantChecker"/>s against a run's <see cref="AgentTrace"/>.
/// These are the deterministic hard gates: any failure fails the run regardless of quality.
/// </summary>
public static class StructuralScorer
{
    /// <summary>
    /// Evaluate the invariants for <paramref name="scenario"/> (its <see cref="Scenario.ExpectedInvariants"/>,
    /// or all <paramref name="checkers"/> when none are specified) against <paramref name="trace"/>.
    /// </summary>
    public static IReadOnlyList<InvariantResult> Score(
        AgentTrace trace, Scenario scenario, IEnumerable<IInvariantChecker> checkers)
    {
        Dictionary<string, IInvariantChecker> byId =
            checkers.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> expected = scenario.ExpectedInvariants.Count > 0
            ? scenario.ExpectedInvariants
            : byId.Keys.ToList();

        List<InvariantResult> results = [];
        foreach (string id in expected)
        {
            if (!byId.TryGetValue(id, out IInvariantChecker? checker))
                continue;
            try
            {
                results.Add(checker.Check(trace, scenario));
            }
            catch (Exception ex)
            {
                results.Add(new InvariantResult
                {
                    Id = id,
                    Passed = false,
                    Severity = checker.Severity,
                    Evidence = $"invariant checker threw: {ex.Message}"
                });
            }
        }
        return results;
    }
}
