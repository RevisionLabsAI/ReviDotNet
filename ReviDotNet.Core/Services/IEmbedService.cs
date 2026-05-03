// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI interface for embedding operations. Replaces the static <c>Embed</c> class.
/// Inject as <c>IEmbedService embed</c> for clean call sites: <c>embed.Generate(...)</c>.
/// </summary>
public interface IEmbedService
{
    // ── Generation ──────────────────────────────────────────────────────

    /// <summary>Generates an embedding vector for a single text input.</summary>
    Task<float[]?> Generate(
        string text,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        int? dimensions = null,
        string? encodingFormat = null,
        string? taskType = null,
        bool? normalize = null,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience overload: generates an embedding using a named model.</summary>
    Task<float[]?> Generate(
        string text,
        string modelName,
        CancellationToken cancellationToken = default);

    /// <summary>Generates embedding vectors for multiple texts in a single batch request.</summary>
    Task<List<float[]>?> GenerateBatch(
        IEnumerable<string> texts,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        int? dimensions = null,
        string? encodingFormat = null,
        string? taskType = null,
        bool? normalize = null,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience overload: generates batch embeddings using a named model.</summary>
    Task<List<float[]>?> GenerateBatch(
        IEnumerable<string> texts,
        string modelName,
        CancellationToken cancellationToken = default);

    // ── Similarity ──────────────────────────────────────────────────────

    /// <summary>Computes cosine similarity between two embedding vectors.</summary>
    float CosineSimilarity(float[] embedding1, float[] embedding2);

    /// <summary>Computes the dot product between two embedding vectors.</summary>
    float DotProduct(float[] embedding1, float[] embedding2);

    /// <summary>Computes Euclidean distance between two embedding vectors.</summary>
    float EuclideanDistance(float[] embedding1, float[] embedding2);

    // ── Search ──────────────────────────────────────────────────────────

    /// <summary>Finds the most similar text from a collection using cosine similarity.</summary>
    Task<(string Text, float Similarity)?> FindMostSimilar(
        string queryText,
        IEnumerable<string> candidateTexts,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken cancellationToken = default);

    /// <summary>Finds the top N most similar texts from a collection.</summary>
    Task<List<(string Text, float Similarity)>?> FindTopSimilar(
        string queryText,
        IEnumerable<string> candidateTexts,
        int topN = 5,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken cancellationToken = default);
}
