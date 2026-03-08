// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Provides a public interface for generating and working with text embeddings from various AI models.
/// This class serves as the main entry point for embedding operations in ReviDotNet, similar to how
/// Infer serves as the interface for inference operations.
/// </summary>
/// <remarks>
/// The Embed class provides methods for:
/// <list type="bullet">
/// <item><description>Generating embeddings for single or multiple text inputs</description></item>
/// <item><description>Computing similarity between embeddings (cosine similarity, dot product)</description></item>
/// <item><description>Finding the most similar texts from a collection</description></item>
/// <item><description>Working with different embedding models and profiles</description></item>
/// </list>
/// All methods are static and thread-safe. The class automatically handles model selection,
/// provider management, and embedding generation through the configured EmbedClient instances.
/// </remarks>
public static class Embed
{
	// ===================
	//  Embedding Generation 
	// ===================
	
	#region Single Embedding Generation
	
	/// <summary>
	/// Generates an embedding vector for a single text input using the specified or default embedding model.
	/// </summary>
	/// <param name="text">The text to generate an embedding for.</param>
	/// <param name="modelProfile">Optional embedding model profile to use. If null, a suitable model will be selected automatically.</param>
	/// <param name="modelName">Optional name of the embedding model to use. If null and modelProfile is null, a default model is selected.</param>
	/// <param name="dimensions">Optional number of dimensions for the embedding vector. If specified, overrides the model's default dimensions (if supported by the model).</param>
	/// <param name="encodingFormat">Optional encoding format for the embedding (e.g., "float", "base64"). If null, uses the model's default format.</param>
	/// <param name="taskType">Optional task type hint for the embedding (e.g., "retrieval_query", "retrieval_document"). Used by some models like Gemini to optimize embeddings.</param>
	/// <param name="normalize">Optional flag to normalize the embedding vector to unit length. If null, uses the model's default normalization behavior.</param>
	/// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
	/// <returns>A float array representing the embedding vector, or null if the operation fails.</returns>
	/// <exception cref="ArgumentException">Thrown when the text is null or empty.</exception>
	/// <exception cref="InvalidOperationException">Thrown when no suitable embedding model can be found or when the model's provider is not properly configured.</exception>
	/// <remarks>
	/// This method automatically selects an appropriate embedding model if one is not specified.
	/// The embedding vector can be used for semantic similarity comparisons, clustering, classification, and other NLP tasks.
	/// </remarks>
	public static async Task<float[]?> Generate(
		string text,
		EmbeddingProfile? modelProfile = null,
		string? modelName = null,
		int? dimensions = null,
		string? encodingFormat = null,
		string? taskType = null,
		bool? normalize = null,
		CancellationToken cancellationToken = default)
	{
		// Validate input
		if (string.IsNullOrWhiteSpace(text))
			throw new ArgumentException("Text cannot be null or empty.", nameof(text));

		// Find the model to use
		EmbeddingProfile model = FindModel(modelProfile, modelName);

		// Validate that the provider has an embedding client
		if (model.Provider?.EmbeddingClient is null)
			throw new InvalidOperationException($"Embedding model '{model.Name}' does not have a valid EmbeddingClient configured.");

		// Use model profile settings as defaults, but allow method parameters to override
		int? effectiveDimensions = dimensions ?? model.Dimensions;
		string? effectiveEncodingFormat = encodingFormat ?? model.EncodingFormat;
		
		// Call the embedding client
		EmbeddingResponse? response = await model.Provider.EmbeddingClient.GenerateEmbeddingAsync(
			input: text,
			model: model.ModelString,
			dimensions: effectiveDimensions,
			encodingFormat: effectiveEncodingFormat,
			cancellationToken: cancellationToken);

		// Extract and return the embedding vector
		if (response?.Data is null || response.Data.Count == 0)
			return null;

		float[] embedding = response.Data[0].Embedding;

		// Apply normalization if requested
		if (normalize == true)
			embedding = NormalizeVector(embedding);

		return embedding;
	}

	/// <summary>
	/// Generates an embedding vector for a single text input using a model specified by name.
	/// This is a convenience overload for the most common use case.
	/// </summary>
	/// <param name="text">The text to generate an embedding for.</param>
	/// <param name="modelName">The name of the embedding model to use.</param>
	/// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
	/// <returns>A float array representing the embedding vector, or null if the operation fails.</returns>
	public static Task<float[]?> Generate(
		string text,
		string modelName,
		CancellationToken cancellationToken = default)
	{
		return Generate(text, null, modelName, null, null, null, null, cancellationToken);
	}

	#endregion

	#region Batch Embedding Generation

	/// <summary>
	/// Generates embedding vectors for multiple text inputs in a single batch request.
	/// </summary>
	/// <param name="texts">The collection of texts to generate embeddings for.</param>
	/// <param name="modelProfile">Optional embedding model profile to use. If null, a suitable model will be selected automatically.</param>
	/// <param name="modelName">Optional name of the embedding model to use. If null and modelProfile is null, a default model is selected.</param>
	/// <param name="dimensions">Optional number of dimensions for the embedding vectors. If specified, overrides the model's default dimensions (if supported by the model).</param>
	/// <param name="encodingFormat">Optional encoding format for the embeddings (e.g., "float", "base64"). If null, uses the model's default format.</param>
	/// <param name="taskType">Optional task type hint for the embeddings (e.g., "retrieval_query", "retrieval_document"). Used by some models like Gemini to optimize embeddings.</param>
	/// <param name="normalize">Optional flag to normalize the embedding vectors to unit length. If null, uses the model's default normalization behavior.</param>
	/// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
	/// <returns>A list of float arrays, where each array represents an embedding vector corresponding to the input texts in order, or null if the operation fails.</returns>
	/// <exception cref="ArgumentException">Thrown when the texts collection is null or empty.</exception>
	/// <exception cref="InvalidOperationException">Thrown when no suitable embedding model can be found or when the model's provider is not properly configured.</exception>
	/// <remarks>
	/// Batch generation is more efficient than generating embeddings one at a time when processing multiple texts.
	/// The order of returned embeddings corresponds to the order of input texts.
	/// </remarks>
	public static async Task<List<float[]>?> GenerateBatch(
		IEnumerable<string> texts,
		EmbeddingProfile? modelProfile = null,
		string? modelName = null,
		int? dimensions = null,
		string? encodingFormat = null,
		string? taskType = null,
		bool? normalize = null,
		CancellationToken cancellationToken = default)
	{
		// Validate input
		if (texts is null)
			throw new ArgumentException("Texts cannot be null.", nameof(texts));

		string[] textArray = texts.ToArray();
		if (textArray.Length == 0)
			throw new ArgumentException("Texts collection cannot be empty.", nameof(texts));

		// Find the model to use
		EmbeddingProfile model = FindModel(modelProfile, modelName);

		// Validate that the provider has an embedding client
		if (model.Provider?.EmbeddingClient is null)
			throw new InvalidOperationException($"Embedding model '{model.Name}' does not have a valid EmbeddingClient configured.");

		// Use model profile settings as defaults, but allow method parameters to override
		int? effectiveDimensions = dimensions ?? model.Dimensions;
		string? effectiveEncodingFormat = encodingFormat ?? model.EncodingFormat;

		// Call the embedding client
		EmbeddingResponse? response = await model.Provider.EmbeddingClient.GenerateEmbeddingsAsync(
			inputs: textArray,
			model: model.ModelString,
			dimensions: effectiveDimensions,
			encodingFormat: effectiveEncodingFormat,
			cancellationToken: cancellationToken);

		// Extract and return the embedding vectors
		if (response?.Data is null || response.Data.Count == 0)
			return null;

		List<float[]> embeddings = response.Data
			.OrderBy(d => d.Index)
			.Select(d => d.Embedding)
			.ToList();

		// Apply normalization if requested
		if (normalize == true)
		{
			for (int i = 0; i < embeddings.Count; i++)
			{
				embeddings[i] = NormalizeVector(embeddings[i]);
			}
		}

		return embeddings;
	}

	/// <summary>
	/// Generates embedding vectors for multiple text inputs using a model specified by name.
	/// This is a convenience overload for the most common use case.
	/// </summary>
	/// <param name="texts">The collection of texts to generate embeddings for.</param>
	/// <param name="modelName">The name of the embedding model to use.</param>
	/// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
	/// <returns>A list of float arrays, where each array represents an embedding vector, or null if the operation fails.</returns>
	public static Task<List<float[]>?> GenerateBatch(
		IEnumerable<string> texts,
		string modelName,
		CancellationToken cancellationToken = default)
	{
		return GenerateBatch(texts, null, modelName, null, null, null, null, cancellationToken);
	}

	#endregion

	// ===================
	//  Similarity Operations 
	// ===================

	#region Similarity Computation

	/// <summary>
	/// Computes the cosine similarity between two embedding vectors.
	/// </summary>
	/// <param name="embedding1">The first embedding vector.</param>
	/// <param name="embedding2">The second embedding vector.</param>
	/// <returns>A similarity score between -1 and 1, where 1 indicates identical direction, 0 indicates orthogonality, and -1 indicates opposite direction.</returns>
	/// <exception cref="ArgumentException">Thrown when either embedding is null or when the embeddings have different dimensions.</exception>
	/// <remarks>
	/// Cosine similarity is a measure of similarity between two non-zero vectors that measures the cosine of the angle between them.
	/// It is commonly used for comparing text embeddings in information retrieval and NLP tasks.
	/// For normalized vectors, cosine similarity is equivalent to dot product.
	/// </remarks>
	public static float CosineSimilarity(float[] embedding1, float[] embedding2)
	{
		if (embedding1 is null)
			throw new ArgumentException("First embedding cannot be null.", nameof(embedding1));
		if (embedding2 is null)
			throw new ArgumentException("Second embedding cannot be null.", nameof(embedding2));
		if (embedding1.Length != embedding2.Length)
			throw new ArgumentException($"Embeddings must have the same dimensions. Got {embedding1.Length} and {embedding2.Length}.");

		float dotProduct = 0f;
		float magnitude1 = 0f;
		float magnitude2 = 0f;

		for (int i = 0; i < embedding1.Length; i++)
		{
			dotProduct += embedding1[i] * embedding2[i];
			magnitude1 += embedding1[i] * embedding1[i];
			magnitude2 += embedding2[i] * embedding2[i];
		}

		magnitude1 = (float)Math.Sqrt(magnitude1);
		magnitude2 = (float)Math.Sqrt(magnitude2);

		if (magnitude1 == 0f || magnitude2 == 0f)
			return 0f;

		return dotProduct / (magnitude1 * magnitude2);
	}

	/// <summary>
	/// Computes the dot product between two embedding vectors.
	/// </summary>
	/// <param name="embedding1">The first embedding vector.</param>
	/// <param name="embedding2">The second embedding vector.</param>
	/// <returns>The dot product of the two vectors.</returns>
	/// <exception cref="ArgumentException">Thrown when either embedding is null or when the embeddings have different dimensions.</exception>
	/// <remarks>
	/// The dot product is a simple and efficient similarity measure.
	/// For normalized vectors (unit vectors), the dot product is equivalent to cosine similarity.
	/// Higher values indicate greater similarity.
	/// </remarks>
	public static float DotProduct(float[] embedding1, float[] embedding2)
	{
		if (embedding1 is null)
			throw new ArgumentException("First embedding cannot be null.", nameof(embedding1));
		if (embedding2 is null)
			throw new ArgumentException("Second embedding cannot be null.", nameof(embedding2));
		if (embedding1.Length != embedding2.Length)
			throw new ArgumentException($"Embeddings must have the same dimensions. Got {embedding1.Length} and {embedding2.Length}.");

		float dotProduct = 0f;
		for (int i = 0; i < embedding1.Length; i++)
		{
			dotProduct += embedding1[i] * embedding2[i];
		}

		return dotProduct;
	}

	/// <summary>
	/// Computes the Euclidean distance between two embedding vectors.
	/// </summary>
	/// <param name="embedding1">The first embedding vector.</param>
	/// <param name="embedding2">The second embedding vector.</param>
	/// <returns>The Euclidean distance between the two vectors. Lower values indicate greater similarity.</returns>
	/// <exception cref="ArgumentException">Thrown when either embedding is null or when the embeddings have different dimensions.</exception>
	/// <remarks>
	/// Euclidean distance (L2 distance) measures the straight-line distance between two points in n-dimensional space.
	/// Unlike cosine similarity and dot product, lower distances indicate greater similarity.
	/// This metric is sensitive to the magnitude of vectors, not just their direction.
	/// </remarks>
	public static float EuclideanDistance(float[] embedding1, float[] embedding2)
	{
		if (embedding1 is null)
			throw new ArgumentException("First embedding cannot be null.", nameof(embedding1));
		if (embedding2 is null)
			throw new ArgumentException("Second embedding cannot be null.", nameof(embedding2));
		if (embedding1.Length != embedding2.Length)
			throw new ArgumentException($"Embeddings must have the same dimensions. Got {embedding1.Length} and {embedding2.Length}.");

		float sumSquaredDifferences = 0f;
		for (int i = 0; i < embedding1.Length; i++)
		{
			float diff = embedding1[i] - embedding2[i];
			sumSquaredDifferences += diff * diff;
		}

		return (float)Math.Sqrt(sumSquaredDifferences);
	}

	#endregion

	#region Similarity Search

	/// <summary>
	/// Finds the most similar text from a collection of candidates based on semantic similarity to a query text.
	/// </summary>
	/// <param name="queryText">The query text to compare against.</param>
	/// <param name="candidateTexts">The collection of candidate texts to search through.</param>
	/// <param name="modelProfile">Optional embedding model profile to use. If null, a suitable model will be selected automatically.</param>
	/// <param name="modelName">Optional name of the embedding model to use.</param>
	/// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
	/// <returns>A tuple containing the most similar text and its similarity score, or null if no candidates are provided or an error occurs.</returns>
	/// <exception cref="ArgumentException">Thrown when queryText is null or empty, or when candidateTexts is null or empty.</exception>
	/// <remarks>
	/// This method generates embeddings for the query and all candidates, then uses cosine similarity to find the best match.
	/// The similarity score ranges from -1 to 1, with higher values indicating greater similarity.
	/// </remarks>
	public static async Task<(string Text, float Similarity)?> FindMostSimilar(
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

		// Generate embedding for the query
		float[]? queryEmbedding = await Generate(queryText, modelProfile, modelName, 
			cancellationToken: cancellationToken);
		if (queryEmbedding is null)
			return null;

		// Generate embeddings for all candidates
		List<float[]>? candidateEmbeddings = await GenerateBatch(candidates, modelProfile, modelName, 
			cancellationToken: cancellationToken);
		if (candidateEmbeddings is null || candidateEmbeddings.Count == 0)
			return null;

		// Find the most similar candidate
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

		if (maxIndex == -1)
			return null;

		return (candidates[maxIndex], maxSimilarity);
	}

	/// <summary>
	/// Finds the top N most similar texts from a collection of candidates based on semantic similarity to a query text.
	/// </summary>
	/// <param name="queryText">The query text to compare against.</param>
	/// <param name="candidateTexts">The collection of candidate texts to search through.</param>
	/// <param name="topN">The number of top results to return.</param>
	/// <param name="modelProfile">Optional embedding model profile to use. If null, a suitable model will be selected automatically.</param>
	/// <param name="modelName">Optional name of the embedding model to use.</param>
	/// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
	/// <returns>A list of tuples containing the most similar texts and their similarity scores, ordered by descending similarity, or null if an error occurs.</returns>
	/// <exception cref="ArgumentException">Thrown when queryText is null or empty, when candidateTexts is null or empty, or when topN is less than 1.</exception>
	/// <remarks>
	/// This method generates embeddings for the query and all candidates, then uses cosine similarity to rank the candidates.
	/// The similarity scores range from -1 to 1, with higher values indicating greater similarity.
	/// If topN is greater than the number of candidates, all candidates will be returned in order of similarity.
	/// </remarks>
	public static async Task<List<(string Text, float Similarity)>?> FindTopSimilar(
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

		// Generate embedding for the query
		float[]? queryEmbedding = await Generate(queryText, modelProfile, modelName, 
			cancellationToken: cancellationToken);
		if (queryEmbedding is null)
			return null;

		// Generate embeddings for all candidates
		List<float[]>? candidateEmbeddings = await GenerateBatch(candidates, modelProfile, modelName, 
			cancellationToken: cancellationToken);
		if (candidateEmbeddings is null || candidateEmbeddings.Count == 0)
			return null;

		// Calculate similarities for all candidates
		var similarities = new List<(string Text, float Similarity)>();
		for (int i = 0; i < candidateEmbeddings.Count; i++)
		{
			float similarity = CosineSimilarity(queryEmbedding, candidateEmbeddings[i]);
			similarities.Add((candidates[i], similarity));
		}

		// Sort by similarity (descending) and take top N
		return similarities
			.OrderByDescending(x => x.Similarity)
			.Take(topN)
			.ToList();
	}

	#endregion

	// ===================
	//  Helper Methods 
	// ===================

	#region Model Selection

	/// <summary>
	/// Finds and returns an appropriate embedding model based on the provided profile or name.
	/// </summary>
	/// <param name="modelProfile">Optional embedding model profile. If provided, this takes precedence.</param>
	/// <param name="modelName">Optional model name to search for. Used if modelProfile is null.</param>
	/// <returns>An EmbeddingProfile representing the selected model.</returns>
	/// <exception cref="InvalidOperationException">Thrown when no suitable model can be found.</exception>
	/// <remarks>
	/// The model selection priority is:
	/// 1. If modelProfile is provided, use it directly
	/// 2. If modelName is provided, search for that specific model
	/// 3. Otherwise, find the best available model using default criteria
	/// </remarks>
	private static EmbeddingProfile FindModel(EmbeddingProfile? modelProfile, string? modelName)
	{
		// If a specific profile is provided, use it
		if (modelProfile is not null)
			return modelProfile;

		// If a model name is specified, try to find it
		if (!string.IsNullOrWhiteSpace(modelName))
		{
			EmbeddingProfile? foundModel = EmbeddingManager.Get(modelName);
			if (foundModel is null)
				throw new InvalidOperationException($"Could not find embedding model with name: {modelName}");
			if (!foundModel.Enabled)
				throw new InvalidOperationException($"Embedding model '{modelName}' is not enabled.");
			return foundModel;
		}

		// Otherwise, find the best available model
		EmbeddingProfile? defaultModel = EmbeddingManager.Find(minTier: ModelTier.C);
		if (defaultModel is null)
			throw new InvalidOperationException("No suitable embedding model could be found. Ensure at least one embedding model is configured and enabled.");

		return defaultModel;
	}

	#endregion

	#region Vector Operations

	/// <summary>
	/// Normalizes a vector to unit length (L2 normalization).
	/// </summary>
	/// <param name="vector">The vector to normalize.</param>
	/// <returns>A new normalized vector.</returns>
	/// <remarks>
	/// Normalization ensures that all vectors have a magnitude of 1, which makes cosine similarity
	/// equivalent to dot product and can improve the performance of certain similarity computations.
	/// If the input vector has zero magnitude, returns a zero vector of the same length.
	/// </remarks>
	private static float[] NormalizeVector(float[] vector)
	{
		float magnitude = 0f;
		for (int i = 0; i < vector.Length; i++)
		{
			magnitude += vector[i] * vector[i];
		}
		magnitude = (float)Math.Sqrt(magnitude);

		if (magnitude == 0f)
			return vector;

		float[] normalized = new float[vector.Length];
		for (int i = 0; i < vector.Length; i++)
		{
			normalized[i] = vector[i] / magnitude;
		}

		return normalized;
	}

	#endregion
}
