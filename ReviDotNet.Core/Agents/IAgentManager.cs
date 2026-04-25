// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI interface for accessing the agent registry. Mirrors IModelManager.
/// </summary>
public interface IAgentManager
{
    AgentProfile? Get(string name);
    List<AgentProfile> GetAll();
}
