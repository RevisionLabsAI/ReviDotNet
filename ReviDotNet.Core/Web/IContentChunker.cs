// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>Splits Markdown into heading-aware, token-bounded chunks for LLM/agent consumption.</summary>
public interface IContentChunker
{
    /// <summary>Produces ordered chunks from a Markdown document.</summary>
    /// <param name="markdown">The Markdown body to split.</param>
    /// <param name="metadata">Document metadata (e.g. title) usable as a root heading.</param>
    /// <param name="options">Chunk sizing/overlap parameters.</param>
    IReadOnlyList<WebChunk> Chunk(string markdown, WebMetadata metadata, ChunkOptions options);
}
