// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Tests.Agents;

/// <summary>
/// Helpers for constructing <see cref="AgentProfile"/> objects from inline .agent text
/// in tests. Round-trips through the real RConfigParser so we exercise the same loading
/// path that a deployed agent goes through.
/// </summary>
internal static class AgentBuilder
{
    /// <summary>
    /// Parses inline .agent text the same way <see cref="AgentManager"/> does at startup.
    /// Returns null if the parsed profile is invalid (e.g. missing required sections).
    /// </summary>
    public static AgentProfile FromText(string agentText)
    {
        var data = RConfigParser.ReadEmbedded(agentText);
        return AgentProfile.ToObject(data);
    }
}
