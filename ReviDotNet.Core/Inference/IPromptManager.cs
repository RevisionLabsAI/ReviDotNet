// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

public interface IPromptManager
{
    Prompt? Get(string name);
}