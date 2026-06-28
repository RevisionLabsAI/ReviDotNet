// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>Severity of an invariant — drives gating weight and reporting.</summary>
public enum InvariantSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// A deterministic, trace-checkable rule the agent MUST satisfy (e.g. "Filer invoked the fact-checker
/// before overriding a contradiction"). A hard gate: any failure fails the run regardless of quality.
/// Implemented by host plugins and run by the Refinery engine over each run's <see cref="AgentTrace"/>.
/// </summary>
public interface IInvariantChecker
{
    /// <summary>Stable id, e.g. "F-2" or "CB-3".</summary>
    string Id { get; }

    /// <summary>Human-readable description of the rule.</summary>
    string Description { get; }

    /// <summary>Severity if violated.</summary>
    InvariantSeverity Severity { get; }

    /// <summary>Decide pass/fail strictly from the run's trace and scenario.</summary>
    InvariantResult Check(AgentTrace trace, Scenario scenario);
}

/// <summary>The outcome of one invariant check against one run.</summary>
public sealed record InvariantResult
{
    /// <summary>The invariant id this result is for.</summary>
    public required string Id { get; init; }

    /// <summary>Whether the invariant held.</summary>
    public required bool Passed { get; init; }

    /// <summary>Severity if violated.</summary>
    public InvariantSeverity Severity { get; init; }

    /// <summary>Concrete evidence (state/step/tool/output excerpt) supporting the verdict.</summary>
    public string Evidence { get; init; } = "";

    /// <summary>Convenience factory for a passing result.</summary>
    public static InvariantResult Pass(IInvariantChecker checker, string evidence = "") =>
        new() { Id = checker.Id, Passed = true, Severity = checker.Severity, Evidence = evidence };

    /// <summary>Convenience factory for a failing result.</summary>
    public static InvariantResult Fail(IInvariantChecker checker, string evidence) =>
        new() { Id = checker.Id, Passed = false, Severity = checker.Severity, Evidence = evidence };
}
