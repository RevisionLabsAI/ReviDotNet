// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi.Refinery;

/// <summary>The outcome of validating a proposed candidate definition before any run budget is spent on it.</summary>
/// <param name="Ok">True when the candidate is safe to register and run.</param>
/// <param name="Errors">Fatal problems that block the candidate (empty when <paramref name="Ok"/> is true).</param>
/// <param name="Warnings">Non-fatal graph/structure warnings surfaced for the ledger (never block acceptance).</param>
public sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    /// <summary>A passing result, optionally carrying non-fatal warnings.</summary>
    public static ValidationResult Pass(IReadOnlyList<string>? warnings = null) =>
        new(true, [], warnings ?? []);

    /// <summary>A failing result carrying one or more blocking errors.</summary>
    public static ValidationResult Fail(params string[] errors) =>
        new(false, errors, []);
}

/// <summary>
/// Statically validates a proposed revised <c>.agent</c> SOURCE before it is registered under the campaign's
/// temp slot and run. Catches the cheap, fatal failures up front — unparseable source, no sections, a source
/// that cannot be turned into an <see cref="AgentProfile"/> — so the loop never burns run budget on a
/// candidate that cannot execute.
/// <para>
/// Validation is deliberately LENIENT about the loop graph: <see cref="AgentProfile.ToObject"/> swallows
/// graph/<c>Init</c> errors (it only logs them), and graph mistakes are collected here as non-fatal
/// <see cref="ValidationResult.Warnings"/> rather than rejections. A candidate is rejected only when it is
/// structurally unusable, never merely because a graph edge looks off — the regression gate, not the
/// validator, is what ultimately decides whether a runnable candidate is good.
/// </para>
/// </summary>
public sealed class CandidateValidator
{
    /// <summary>
    /// Validate <paramref name="revisedSource"/> (a full <c>.agent</c> source) as if it were about to be
    /// registered under <paramref name="tempName"/>. Returns a failing result with a specific reason when the
    /// candidate cannot be parsed or turned into a profile; otherwise passes, carrying any graph warnings.
    /// </summary>
    public ValidationResult Validate(string revisedSource, string tempName)
    {
        if (string.IsNullOrWhiteSpace(revisedSource))
            return ValidationResult.Fail("Candidate definition is empty.");

        // (1) Parse the flat RConfig dictionary. A malformed source throws IOException here.
        Dictionary<string, string> data;
        try
        {
            data = RConfigParser.ReadEmbedded(revisedSource);
        }
        catch (Exception ex)
        {
            return ValidationResult.Fail($"Candidate source failed to parse: {ex.Message}");
        }

        if (data.Count == 0)
            return ValidationResult.Fail("Candidate source has no recognizable [[sections]].");

        // (2) Build the profile (ToObject swallows graph/Init errors — it only logs them). A throw here is a
        // hard structural failure (e.g. a property that cannot be converted at all).
        AgentProfile candidate;
        try
        {
            candidate = AgentProfile.ToObject(data, namePrefix: "");
        }
        catch (Exception ex)
        {
            return ValidationResult.Fail($"Candidate could not be built into an agent profile: {ex.Message}");
        }

        // The temp name becomes the registration key; the internal graph never references it, so applying it
        // here mirrors RegisterCandidate and lets ValidateGraph attribute warnings to a stable name.
        candidate.Name = tempName;

        // (3) Collect graph warnings (never fatal). ValidateGraph never throws and returns [] for a clean or
        // graph-less definition (e.g. a prompt-only system-prompt revision with no [[_loop]]).
        List<string> warnings;
        try
        {
            warnings = candidate.ValidateGraph();
        }
        catch
        {
            warnings = [];
        }

        return ValidationResult.Pass(warnings);
    }
}
