// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

public enum Protocol
{
    OpenAI,
    vLLM,
    Gemini,
    Perplexity,
    LLamaAPI, // Not implemented
    /// <summary>Anthropic Messages API. Text, streaming, thinking and structured output through <see cref="InferClient"/>; native tool use through <see cref="Revi.ToolUse.ToolChatClient"/>.</summary>
    Claude,

    /// <summary>OpenRouter's OpenAI-compatible API with provider-routing extensions.</summary>
    OpenRouter,

    /// <summary>Typed decisions over shared state; independent of provider and model family.</summary>
    SystemOne
}
