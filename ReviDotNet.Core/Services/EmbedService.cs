// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI service implementation of <see cref="IEmbedService"/>. Replaces the static <c>Embed</c> class.
/// Uses the injected <see cref="IEmbeddingManager"/> for model resolution.
/// </summary>
public sealed class EmbedService(
    IEmbeddingManager embeddings,
    IReviLogger<EmbedService> logger) : IEmbedService
{
    // ===================
    //  Single Embedding Generation
    // ===================

    /// <inheritdoc/>
    public async Task<float[]?> Generate(
        string text,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        int? dimensions = null,
        string? encodingFormat = null,
        string? taskType = null,
        bool? normalize = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or empty.", nameof(text));

        EmbeddingProfile model = FindModel(modelProfile, modelName);

        if (model.Provider?.EmbeddingClient is null)
            throw new InvalidOperationException($"Embedding model '{model.Name}' does not have a valid EmbeddingClient configured.");

        int? effectiveDimensions = dimensions ?? model.Dimensions;
        string? effectiveEncodingFormat = encodingFormat ?? model.EncodingFormat;

        EmbeddingResponse? response = await model.Provider.EmbeddingClient.GenerateEmbeddingAsync(
            input: text,
            model: model.ModelString,
            dimensions: effectiveDimensions,
            encodingFormat: effectiveEncodingFormat,
            cancellationToken: cancellationToken);

        if (response?.Data is null || response.Data.Count == 0)
            return null;

        float[] embedding = response.Data[0].Embedding;

        if (normalize == true)
            embedding = NormalizeVector(embedding);

        return embedding;
    }

    /// <inheritdoc/>
    public Task<float[]?> Generate(
        string text,
        string modelName,
        CancellationToken cancellationToken = default)
        => Generate(text, null, modelName, null, null, null, null, cancellationToken);

    // ===================
    //  Batch Embedding Generation
    // ===================

    /// <inheritdoc/>
    public async Task<List<float[]>?> GenerateBatch(
        IEnumerable<string> texts,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        int? dimensions = null,
        string? encodingFormat = null,
        string? taskType = null,
        bool? normalize = null,
        CancellationToken cancellationToken = default)
    {
        if (texts is null)
            throw new ArgumentException("Texts cannot be null.", nameof(texts));

        string[] textArray = texts.ToArray();
        if (textArray.Length == 0)
            throw new ArgumentException("Texts collection cannot be empty.", nameof(texts));

        EmbeddingProfile model = FindModel(modelProfile, modelName);

        if (model.Provider?.EmbeddingClient is null)
            throw new InvalidOperationException($"Embedding model '{model.Name}' does not have a valid EmbeddingClient configured.");

        int? effectiveDimensions = dimensions ?? model.Dimensions;
        string? effectiveEncodingFormat = encodingFormat ?? model.EncodingFormat;

        EmbeddingResponse? response = await model.Provider.EmbeddingClient.GenerateEmbeddingsAsync(
            inputs: textArray,
            model: model.ModelString,
            dimensions: effectiveDimensions,
            encodingFormat: effectiveEncodingFormat,
            cancellationToken: cancellationToken);

        if (response?.Data is null || response.Data.Count == 0)
            return null;

        List<float[]> embeddingList = response.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();

        if (normalize == true)
        {
            for (int i = 0; i < embeddingList.Count; i++)
                embeddingList[i] = NormalizeVector(embeddingList[i]);
        }

        return embeddingList;
    }

    /// <inheritdoc/>
    public Task<List<float[]>?> GenerateBatch(
        IEnumerable<string> texts,
        string modelName,
        CancellationToken cancellationToken = default)
        => GenerateBatch(texts, null, modelName, null, null, null, null, cancellationToken);

    // ===================
    //  Similarity Computation
    // ===================

    /// <inheritdoc/>
    public float CosineSimilarity(float[] embedding1, float[] embedding2)
    {
        ValidateEmbeddings(embedding1, embedding2);

        float dotProduct = 0f, magnitude1 = 0f, magnitude2 = 0f;
        for (int i = 0; i < embedding1.Length; i++)
        {
            dotProduct += embedding1[i] * embedding2[i];
            magnitude1 += embedding1[i] * embedding1[i];
            magnitude2 += embedding2[i] * embedding2[i];
        }

        magnitude1 = (float)Math.Sqrt(magnitude1);
        magnitude2 = (float)Math.Sqrt(magnitude2);

        if (magnitude1 == 0f || magnitude2 == 0f) return 0f;
        return dotProduct / (magnitude1 * magnitude2);
    }

    /// <inheritdoc/>
    public float DotProduct(float[] embedding1, float[] embedding2)
    {
        ValidateEmbeddings(embedding1, embedding2);

        float result = 0f;
        for (int i = 0; i < embedding1.Length; i++)
            result += embedding1[i] * embedding2[i];
        return result;
    }

    /// <inheritdoc/>
    public float EuclideanDistance(float[] embedding1, float[] embedding2)
    {
        ValidateEmbeddings(embedding1, embedding2);

        float sumSquared = 0f;
        for (int i = 0; i < embedding1.Length; i++)
        {
            float diff = embedding1[i] - embedding2[i];
            sumSquared += diff * diff;
        }
        return (float)Math.Sqrt(sumSquared);
    }

    // ===================
    //  Similarity Search
    // ===================

    /// <inheritdoc/>
    public async Task<(string Text, float Similarity)?> FindMostSimilar(
        string queryText,
        IEnumerable<string> candidateTexts,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            throw new ArgumentException("Query text cannot be null or empty.", nameof(queryText));
        if (candidateTexts is null)
            throw new ArgumentException("Candidate texts cannot be null.", nameof(candidateTexts));

        string[] candidates = candidateTexts.ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException("Candidate texts collection cannot be empty.", nameof(candidateTexts));

        float[]? queryEmbedding = await Generate(queryText, modelProfile, modelName, cancellationToken: cancellationToken);
        if (queryEmbedding is null) return null;

        List<float[]>? candidateEmbeddings = await GenerateBatch(candidates, modelProfile, modelName, cancellationToken: cancellationToken);
        if (candidateEmbeddings is null || candidateEmbeddings.Count == 0) return null;

        float maxSimilarity = float.MinValue;
        int maxIndex = -1;
        for (int i = 0; i < candidateEmbeddings.Count; i++)
        {
            float similarity = CosineSimilarity(queryEmbedding, candidateEmbeddings[i]);
            if (similarity > maxSimilarity)
            {
                maxSimilarity = similarity;
                maxIndex = i;
            }
        }

        if (maxIndex == -1) return null;
        return (candidates[maxIndex], maxSimilarity);
    }

    /// <inheritdoc/>
    public async Task<List<(string Text, float Similarity)>?> FindTopSimilar(
        string queryText,
        IEnumerable<string> candidateTexts,
        int topN = 5,
        EmbeddingProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            throw new ArgumentException("Query text cannot be null or empty.", nameof(queryText));
        if (candidateTexts is null)
            throw new ArgumentException("Candidate texts cannot be null.", nameof(candidateTexts));
        if (topN < 1)
            throw new ArgumentException("topN must be at least 1.", nameof(topN));

        string[] candidates = candidateTexts.ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException("Candidate texts collection cannot be empty.", nameof(candidateTexts));

        float[]? queryEmbedding = await Generate(queryText, modelProfile, modelName, cancellationToken: cancellationToken);
        if (queryEmbedding is null) return null;

        List<float[]>? candidateEmbeddings = await GenerateBatch(candidates, modelProfile, modelName, cancellationToken: cancellationToken);
        if (candidateEmbeddings is null || candidateEmbeddings.Count == 0) return null;

        List<(string Text, float Similarity)> similarities = [];
        for (int i = 0; i < candidateEmbeddings.Count; i++)
            similarities.Add((candidates[i], CosineSimilarity(queryEmbedding, candidateEmbeddings[i])));

        return similarities
            .OrderByDescending(x => x.Similarity)
            .Take(topN)
            .ToList();
    }

    // ===================
    //  Private Helpers
    // ===================

    private EmbeddingProfile FindModel(EmbeddingProfile? modelProfile, string? modelName)
    {
        if (modelProfile is not null)
            return modelProfile;

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            EmbeddingProfile? found = embeddings.Get(modelName);
            if (found is null)
                throw new InvalidOperationException($"Could not find embedding model with name: {modelName}");
            if (!found.Enabled)
                throw new InvalidOperationException($"Embedding model '{modelName}' is not enabled.");
            return found;
        }

        EmbeddingProfile? defaultModel = embeddings.Find(minTier: ModelTier.C);
        if (defaultModel is null)
            throw new InvalidOperationException("No suitable embedding model could be found. Ensure at least one embedding model is configured and enabled.");

        return defaultModel;
    }

    private static void ValidateEmbeddings(float[] embedding1, float[] embedding2)
    {
        if (embedding1 is null) throw new ArgumentException("First embedding cannot be null.", nameof(embedding1));
        if (embedding2 is null) throw new ArgumentException("Second embedding cannot be null.", nameof(embedding2));
        if (embedding1.Length != embedding2.Length)
            throw new ArgumentException($"Embeddings must have the same dimensions. Got {embedding1.Length} and {embedding2.Length}.");
    }

    private static float[] NormalizeVector(float[] vector)
    {
        float magnitude = 0f;
        for (int i = 0; i < vector.Length; i++)
            magnitude += vector[i] * vector[i];
        magnitude = (float)Math.Sqrt(magnitude);

        if (magnitude == 0f) return vector;

        float[] normalized = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
            normalized[i] = vector[i] / magnitude;
        return normalized;
    }
}
