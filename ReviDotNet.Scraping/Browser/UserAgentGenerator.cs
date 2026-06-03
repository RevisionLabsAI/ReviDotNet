// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Generates plausible modern Chrome user-agent strings by rotating platform and major-version tokens.
/// </summary>
public static class UserAgentGenerator
{
    private static readonly Random Rng = new();

    private static readonly string[] Platforms =
    {
        "Windows NT 10.0; Win64; x64",
        "Windows NT 11.0; Win64; x64",
        "Macintosh; Intel Mac OS X 10_15_7",
        "Macintosh; Intel Mac OS X 13_6",
        "X11; Linux x86_64"
    };

    private static readonly (int major, int minor, int build)[] ChromeVersions =
    {
        (124,0,0), (125,0,0), (126,0,0), (127,0,0), (128,0,0)
    };

    /// <summary>Returns a randomized but plausible Chrome user-agent string.</summary>
    public static string GenerateChrome()
    {
        string platform = Pick(Platforms);
        (int major, int minor, int build) ver = Pick(ChromeVersions);
        int build = Rng.Next(0, 6000);
        // Chrome/major.minor.build.patch format: create plausible numbers
        string chrome = $"Chrome/{ver.major}.{ver.minor}.{build}.{Rng.Next(0,400)}";
        return $"Mozilla/5.0 ({platform}) AppleWebKit/537.36 (KHTML, like Gecko) {chrome} Safari/537.36";
    }

    private static T Pick<T>(IReadOnlyList<T> arr) => arr[Rng.Next(arr.Count)];
}
