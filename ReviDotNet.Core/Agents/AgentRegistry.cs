// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI facade that implements IAgentManager by delegating to the static AgentManager.
/// Register as a singleton in your service collection alongside ModelRegistry, PromptRegistry, etc.
/// </summary>
public sealed class AgentRegistry : IAgentManager
{
    public AgentProfile? Get(string name) => AgentManager.Get(name);
    public List<AgentProfile> GetAll() => AgentManager.GetAll();
}
