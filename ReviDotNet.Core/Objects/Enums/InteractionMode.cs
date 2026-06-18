// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// How an agent may be driven from the workshop. <see cref="Fixed"/> agents run autonomously to
/// completion on an initial input; <see cref="Chat"/> agents are driven interactively, one user
/// message per turn (each turn re-runs the agent seeded with the prior conversation);
/// <see cref="Both"/> agents support either. Configured via <c>settings_interaction-mode</c> in a
/// <c>.agent</c> file (<c>fixed</c> / <c>chat</c> / <c>both</c>); defaults to <see cref="Fixed"/>.
/// </summary>
public enum InteractionMode
{
    Fixed,
    Chat,
    Both
}
