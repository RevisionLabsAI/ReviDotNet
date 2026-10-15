// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>
/// Stops a campaign once a provider has refused it in a way no later run can get past: the account
/// cannot pay (<see cref="InferenceFailureKind.Billing"/>) or the key was rejected
/// (<see cref="InferenceFailureKind.Authentication"/>). Every run after that would fail the same way,
/// and paying the judge to grade those failures buys nothing.
/// <para>
/// It listens to <see cref="InferenceProviderMonitor"/>, which reports every classified inference
/// outcome process-wide. Only outcomes raised in the async flow that created the watch count, so two
/// campaigns in one host (Forge) never stop each other. The agent under test and the meta calls (judge,
/// pairwise gate, proposer) both run in that flow, so a refusal from either one stops the campaign.
/// </para>
/// <para>
/// It is checked before each run starts. Run one at a time (as every dollar-capped campaign is), nothing
/// starts after the refusal; run in parallel, the runs already in flight still finish.
/// </para>
/// </summary>
public sealed class ProviderHaltWatch : IDisposable
{
    private static readonly AsyncLocal<ProviderHaltWatch?> Active = new();

    private readonly ProviderHaltWatch? _previous;
    private volatile string? _reason;

    /// <summary>Starts watching, and makes this watch <see cref="Current"/> for the calling async flow.</summary>
    public ProviderHaltWatch()
    {
        _previous = Active.Value;
        Active.Value = this;
        InferenceProviderMonitor.OutcomeObserved += OnOutcome;
    }

    /// <summary>The watch for the calling async flow, or null when no campaign is running in it.</summary>
    public static ProviderHaltWatch? Current => Active.Value;

    /// <summary>True once a provider has refused the campaign for good.</summary>
    public bool Halted => _reason is not null;

    /// <summary>Which provider refused and why, or null while nothing has.</summary>
    public string? Reason => _reason;

    /// <summary>Whether a failure of this kind means no later call to that provider can succeed.</summary>
    /// <param name="kind">The classified failure kind.</param>
    /// <returns>True for billing and authentication failures.</returns>
    public static bool StopsTheCampaign(InferenceFailureKind kind) =>
        kind is InferenceFailureKind.Billing or InferenceFailureKind.Authentication;

    /// <summary>Throws <see cref="ProviderHaltException"/> when a provider has refused the campaign.</summary>
    /// <exception cref="ProviderHaltException">A provider refused the campaign.</exception>
    public void ThrowIfHalted()
    {
        string? reason = _reason;
        if (reason is not null)
            throw new ProviderHaltException(reason);
    }

    /// <summary>Stops listening and restores the previous watch for this flow.</summary>
    public void Dispose()
    {
        InferenceProviderMonitor.OutcomeObserved -= OnOutcome;
        if (ReferenceEquals(Active.Value, this))
            Active.Value = _previous;
    }

    /// <summary>Records the first campaign-stopping failure raised in this watch's flow.</summary>
    /// <param name="outcome">The classified outcome.</param>
    private void OnOutcome(InferenceProviderOutcome outcome)
    {
        if (outcome.Succeeded || _reason is not null || !StopsTheCampaign(outcome.Failure.Kind))
            return;
        if (!ReferenceEquals(Active.Value, this))
            return;

        string model = string.IsNullOrWhiteSpace(outcome.ModelName) ? "" : $" ({outcome.ModelName})";
        _reason = $"provider '{outcome.ProviderName}'{model} refused the campaign: {outcome.Failure}. Every later run would fail the same way; fix the key or the account and run again.";
    }
}

/// <summary>Thrown before the next run once a provider has refused a campaign for good.</summary>
public sealed class ProviderHaltException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which provider refused and why.</param>
    public ProviderHaltException(string message)
        : base(message)
    {
    }
}
