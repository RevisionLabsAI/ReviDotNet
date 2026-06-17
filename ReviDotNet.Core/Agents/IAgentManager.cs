// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>DI interface for the agent profile registry.</summary>
public interface IAgentManager
{
    /// <summary>Loads agent profiles from the application assembly.</summary>
    Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default);

    /// <summary>Returns the agent profile with the given name, or null if not found.</summary>
    AgentProfile? Get(string name);

    /// <summary>Returns all loaded agent profiles.</summary>
    List<AgentProfile> GetAll();

    /// <summary>Programmatically adds an agent profile to the registry.</summary>
    void Add(AgentProfile agent);

    /// <summary>
    /// Adds an agent profile, replacing any existing profile with the same name.
    /// Used to apply in-memory edits to agents that have no writable file on disk
    /// (e.g. embedded-resource agents) so subsequent runs pick up the change.
    /// </summary>
    void AddOrReplace(AgentProfile agent);
}
