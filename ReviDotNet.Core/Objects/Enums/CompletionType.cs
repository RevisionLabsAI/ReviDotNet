// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

public enum CompletionType
{
    /// <summary>
    /// 
    /// </summary>
    ChatOnly,
    
    /// <summary>
    /// 
    /// </summary>
    PromptOnly,
    
    /// <summary>
    /// Prefers prompt completion, but falls back to chat with examples in the same message if necessary.
    /// </summary>
    PromptChatOne, 
    
    /// <summary>
    /// Prefers prompt completion, but falls back to chat with examples as separate messages if necessary.
    /// </summary>
    PromptChatMulti
}