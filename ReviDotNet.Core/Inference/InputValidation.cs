// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;

namespace Revi;

/// <summary>
/// Runtime validation of prompt input usage, shared by the chat and prompt message builders. Detects the
/// two common authoring mistakes that otherwise fail silently in production:
/// <list type="bullet">
/// <item>an unfilled <c>{placeholder}</c> left in the rendered prompt (no matching input was provided), and</item>
/// <item>a provided input that matched no placeholder and was silently dropped.</item>
/// </list>
/// Findings are logged as warnings; if the prompt sets <c>strict-inputs = true</c> they throw instead.
/// </summary>
internal static class InputValidation
{
    // Same placeholder shape the REVI003 analyzer recognizes: {Identifier} with letters/digits/space/_/-.
    // The character class excludes quotes/colons/braces, so JSON like {"k": 1} in a body is not matched.
    private static readonly Regex PlaceholderRegex =
        new(@"\{\s*([A-Za-z0-9 _\-]+?)\s*\}", RegexOptions.Compiled);

    /// <summary>
    /// Validates input usage after substitution and warns (or throws under strict mode).
    /// </summary>
    /// <param name="prompt">The prompt being rendered (supplies name and the strict-inputs flag).</param>
    /// <param name="inputs">All inputs supplied for this call.</param>
    /// <param name="segments">Rendered text segments paired with whether that segment underwent
    /// fill-mode substitution (only filled segments can legitimately leave unfilled placeholders).</param>
    /// <param name="matchedIdentifiers">Identifiers of inputs that successfully filled a placeholder.</param>
    /// <param name="unmatchedInputsAreDropped">True when unmatched inputs are not also emitted as a listed
    /// section (i.e. they are genuinely lost); only then is an unmatched input worth warning about.</param>
    public static void Check(
        Prompt prompt,
        IReadOnlyCollection<Input>? inputs,
        IEnumerable<(string? Text, bool FilledMode)> segments,
        ISet<string> matchedIdentifiers,
        bool unmatchedInputsAreDropped)
    {
        List<string> warnings = new();
        HashSet<string> seenPlaceholders = new(StringComparer.OrdinalIgnoreCase);

        // (a) Unfilled placeholders remaining in filled-mode segments.
        foreach ((string? text, bool filledMode) in segments)
        {
            if (!filledMode || string.IsNullOrEmpty(text))
                continue;

            foreach (Match m in PlaceholderRegex.Matches(text))
            {
                string token = m.Groups[1].Value.Trim();
                if (seenPlaceholders.Add(token))
                    warnings.Add($"placeholder '{{{token}}}' was not filled (no matching input provided)");
            }
        }

        // (b) Provided inputs that matched no placeholder and were dropped (not listed elsewhere).
        if (unmatchedInputsAreDropped && inputs != null)
        {
            foreach (Input input in inputs)
            {
                if (!matchedIdentifiers.Contains(input.Identifier))
                    warnings.Add($"input '{input.Identifier}' did not match any placeholder and was dropped");
            }
        }

        if (warnings.Count == 0)
            return;

        string joined = string.Join("; ", warnings);
        if (prompt.StrictInputs == true)
            throw new Exception($"Strict input validation failed for prompt '{prompt.Name}': {joined}");

        Util.Log($"Warning: input/placeholder mismatch in prompt '{prompt.Name}': {joined}");
    }
}
