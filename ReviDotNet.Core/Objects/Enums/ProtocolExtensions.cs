// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Describes shared wire-format capabilities implemented by provider protocols.
/// </summary>
public static class ProtocolExtensions
{
    /// <summary>
    /// Determines whether a protocol uses OpenAI-compatible endpoints, bearer authentication,
    /// payloads, and response envelopes.
    /// </summary>
    /// <param name="protocol">The provider protocol to classify.</param>
    /// <returns><see langword="true"/> when the protocol uses an OpenAI-compatible wire format.</returns>
    public static bool UsesOpenAiWireFormat(this Protocol protocol)
    {
        return protocol is Protocol.OpenAI
            or Protocol.OpenRouter
            or Protocol.vLLM
            or Protocol.Perplexity
            or Protocol.LLamaAPI;
    }

    /// <summary>
    /// Determines whether the protocol may call the OpenAI-compatible Responses API when the
    /// provider profile also declares that endpoint as supported.
    /// </summary>
    /// <param name="protocol">The provider protocol to classify.</param>
    /// <returns><see langword="true"/> for protocols with a supported Responses API implementation.</returns>
    public static bool UsesOpenAiResponsesApi(this Protocol protocol)
    {
        return protocol is Protocol.OpenAI or Protocol.OpenRouter;
    }
}
