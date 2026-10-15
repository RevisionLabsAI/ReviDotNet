// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// The campaign's dollar cap (<see cref="CampaignSpec.MaxCostUsd"/>): what the agent under test and the
/// meta-LLM calls (judge, pairwise gate, proposer) have cost so far, and whether one more run may start.
/// <para>
/// Unlike the token budgets, it is asked <b>before every scenario run</b>, not only between rounds, so a
/// baseline-only campaign is bounded too. A run may start only while
/// <c>spent + the most expensive run so far &lt;= cap</c>: the next run is assumed to cost as much as the
/// worst one seen, so the campaign stops before a run that would likely cross the cap rather than after.
/// The first run is allowed whenever anything is left, since nothing is known about its cost yet.
/// </para>
/// <para>
/// Meta spend is read from <see cref="MetaLlmUsageBroker.SpentUsd"/>, which prices each call from the
/// model's configured rates (a conservative fallback rate when the model has none). Agent spend is the
/// run's own <see cref="EfficiencyMetrics.CostUsd"/>, priced by the agent runner the same way.
/// </para>
/// </summary>
public sealed class CostGovernor
{
    private readonly object _gate = new();
    private readonly Func<decimal> _metaSpent;
    private decimal _agentSpent;
    private decimal _largestRun;

    /// <summary>Creates a governor for one campaign.</summary>
    /// <param name="cap">The cap in USD, or null for no cap.</param>
    /// <param name="metaSpent">Reads the campaign's meta-LLM spend so far, in USD.</param>
    public CostGovernor(decimal? cap, Func<decimal> metaSpent)
    {
        Cap = cap;
        _metaSpent = metaSpent ?? throw new ArgumentNullException(nameof(metaSpent));
    }

    /// <summary>The cap in USD, or null when the campaign has none.</summary>
    public decimal? Cap { get; }

    /// <summary>What the campaign has cost so far: agent runs plus meta-LLM calls, in USD.</summary>
    public decimal Spent
    {
        get
        {
            lock (_gate) return _agentSpent + _metaSpent();
        }
    }

    /// <summary>The most one run (agent plus its meta calls) has cost so far, in USD.</summary>
    public decimal LargestRun
    {
        get
        {
            lock (_gate) return _largestRun;
        }
    }

    /// <summary>True once a run was refused because the cap would have been crossed.</summary>
    public bool Stopped { get; private set; }

    /// <summary>
    /// Whether one more run may start. Records the refusal, so <see cref="Stopped"/> reports it afterwards.
    /// </summary>
    /// <returns>True when there is no cap, or when the spend plus the largest run so far stays within it.</returns>
    public bool TryStartRun()
    {
        if (Cap is not { } cap) return true;

        lock (_gate)
        {
            decimal spent = _agentSpent + _metaSpent();
            bool allowed = spent < cap && spent + _largestRun <= cap;
            if (!allowed) Stopped = true;
            return allowed;
        }
    }

    /// <summary>Records one finished run's cost.</summary>
    /// <param name="agentCost">What the agent under test cost, in USD.</param>
    /// <param name="metaCost">What the meta-LLM calls made for this run cost, in USD.</param>
    public void RecordRun(decimal agentCost, decimal metaCost)
    {
        lock (_gate)
        {
            _agentSpent += Math.Max(0, agentCost);
            _largestRun = Math.Max(_largestRun, Math.Max(0, agentCost) + Math.Max(0, metaCost));
        }
    }

    /// <summary>A one-line description of the stop, for the campaign's progress log.</summary>
    /// <returns>The description.</returns>
    public string Describe() =>
        Cap is { } cap
            ? $"cost cap ${cap:0.00} reached: ${Spent:0.0000} spent, largest run ${LargestRun:0.0000}"
            : "no cost cap";
}
