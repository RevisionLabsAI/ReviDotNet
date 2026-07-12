// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Detects degenerate repetition loops in model output — the small-model failure mode of emitting the
/// same token run, line, or paragraph until the output ceiling. Configured per model via
/// <c>[[settings]] loop-detection</c> with a named algorithm:
/// <para>
/// <b><c>repeat-N</c></b> (e.g. <c>repeat-512</c>): trips when the TRAILING text consists of at least
/// <see cref="MinRepeats"/> consecutive EXACT repeats of the same unit, spanning ≥ N characters in total.
/// Detection is suffix-anchored (a loop the model recovered from mid-output does not trip) and exact-match
/// only — legitimate repetitive structure (tables, JSON arrays of similar objects, code boilerplate) varies
/// by at least a few characters per row and therefore never forms an exact period, which is what keeps the
/// false-positive rate near zero. Comparison runs on a whitespace-normalized view so a loop that varies only
/// in line breaks or padding still trips.
/// </para>
/// Cost: one Z-array over a bounded tail window — O(window) per call, allocation-light.
/// </summary>
public static class RepetitionDetector
{
    /// <summary>Minimum consecutive exact repeats of a unit before a loop is declared.</summary>
    public const int MinRepeats = 4;

    /// <summary>Detection never triggers before this much total output exists (false-positive floor).</summary>
    public const int MinOutputChars = 512;

    /// <summary>The largest tail window examined, regardless of N (bounds per-call cost).</summary>
    public const int MaxWindow = 16384;

    /// <summary>
    /// Parses a <c>loop-detection</c> algorithm string. Returns false (and 0) for null/empty — detection
    /// off — and throws <see cref="FormatException"/> for a non-empty string that is not a known algorithm,
    /// so a typo in a model config fails the file load loudly instead of silently disabling the breaker.
    /// </summary>
    public static bool TryParseAlgorithm(string? algorithm, out int minSpan)
    {
        minSpan = 0;
        if (string.IsNullOrWhiteSpace(algorithm))
            return false;

        string a = algorithm.Trim();
        if (a.StartsWith("repeat-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(a["repeat-".Length..], out int n) && n > 0)
        {
            minSpan = n;
            return true;
        }

        throw new FormatException(
            $"Unknown loop-detection algorithm '{algorithm}'. Supported: repeat-N (e.g. repeat-512).");
    }

    /// <summary>
    /// Runs the configured detection over <paramref name="text"/>. Returns true when a degenerate loop is
    /// detected at the END of the text, with a short human-readable <paramref name="evidence"/> naming the
    /// repeated unit and counts. Returns false for null/short text or when <paramref name="algorithm"/> is
    /// unset. Never throws on content (only on an unparseable algorithm string).
    /// </summary>
    public static bool TryDetect(string? text, string? algorithm, out string evidence)
    {
        evidence = string.Empty;
        if (!TryParseAlgorithm(algorithm, out int minSpan))
            return false;
        if (string.IsNullOrEmpty(text) || text.Length < Math.Max(MinOutputChars, minSpan))
            return false;

        // Normalize whitespace so "unit\nunit\nunit" and "unit unit unit" loop-detect identically, and a
        // loop that drifts only in padding still forms an exact period.
        string normalized = NormalizeWhitespace(text);
        if (normalized.Length < Math.Max(MinOutputChars, minSpan))
            return false;

        int window = Math.Min(normalized.Length, Math.Max(4 * minSpan, 4096));
        window = Math.Min(window, MaxWindow);
        string tail = normalized[^window..];

        // Suffix-periodicity via a Z-array over the REVERSED tail: for a shift p, z[p] is the length of the
        // match between the reversed tail and itself shifted by p — i.e. the tail's last z[p] characters
        // equal the z[p] characters ending p earlier. A suffix of length z[p] + p therefore has period p.
        char[] reversed = tail.ToCharArray();
        Array.Reverse(reversed);
        int[] z = ZArray(reversed);

        // Smallest period wins (report "ab" ×200, not "abab" ×100).
        for (int p = 1; p <= tail.Length / MinRepeats; p++)
        {
            long span = (long)z[p] + p;
            if (span >= minSpan && span / p >= MinRepeats)
            {
                string unit = tail[^p..];
                int repeats = (int)(span / p);
                string shownUnit = unit.Length <= 60 ? unit : unit[..57] + "…";
                evidence = $"trailing loop: unit of {p} char(s) repeated {repeats}× spanning {span} chars — \"{shownUnit}\"";
                return true;
            }
        }

        return false;
    }

    /// <summary>Collapses whitespace runs to single spaces (loops that drift only in padding still match).</summary>
    private static string NormalizeWhitespace(string text)
    {
        System.Text.StringBuilder sb = new(text.Length);
        bool inWs = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWs) sb.Append(' ');
                inWs = true;
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>Standard Z-array: z[i] = length of the longest common prefix of s and s[i..]. O(n).</summary>
    private static int[] ZArray(char[] s)
    {
        int n = s.Length;
        int[] z = new int[n];
        z[0] = n;
        int l = 0, r = 0;
        for (int i = 1; i < n; i++)
        {
            if (i < r)
                z[i] = Math.Min(r - i, z[i - l]);
            while (i + z[i] < n && s[z[i]] == s[i + z[i]])
                z[i]++;
            if (i + z[i] > r)
            {
                l = i;
                r = i + z[i];
            }
        }
        return z;
    }
}
