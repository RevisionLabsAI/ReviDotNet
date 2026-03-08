// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Represents a single embedding data object with its vector and metadata.
/// </summary>
public class EmbeddingData
{
    /// <summary>
    /// The embedding vector as an array of floats.
    /// </summary>
    public float[] Embedding { get; set; }
    
    /// <summary>
    /// The index of this embedding in the response (for batch requests).
    /// </summary>
    public int Index { get; set; }
    
    /// <summary>
    /// The object type (typically "embedding").
    /// </summary>
    public string Object { get; set; }
}

/// <summary>
/// Represents the response structure for embedding requests.
/// </summary>
public class EmbeddingResponse
{
    /// <summary>
    /// The input text(s) that were embedded.
    /// </summary>
    public List<string> Inputs { get; set; }
    
    /// <summary>
    /// List of embedding data objects containing the vectors.
    /// </summary>
    public List<EmbeddingData> Data { get; set; }
    
    /// <summary>
    /// The model used to generate the embeddings.
    /// </summary>
    public string Model { get; set; }
    
    /// <summary>
    /// The object type (typically "list").
    /// </summary>
    public string Object { get; set; }
    
    /// <summary>
    /// Usage information (token counts, if available).
    /// </summary>
    public Dictionary<string, int>? Usage { get; set; }
}
