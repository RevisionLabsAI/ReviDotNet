// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace Revi;

/// <summary>
/// Represents a configuration profile for an embedding model.
/// Contains model identity, provider information, token limits, and embedding-specific settings.
/// This profile is specifically designed for text embedding models and excludes inference-only properties.
/// </summary>
public class EmbeddingProfile
{
    // ================================
    //  EmbeddingProfile Object Definition
    // ================================
    
    #region Core Identity
    
    /// <summary>
    /// Gets or sets the unique name identifier for this embedding model profile.
    /// </summary>
    [RConfigProperty("general_name")]
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets whether this embedding model profile is enabled and available for use.
    /// </summary>
    [RConfigProperty("general_enabled")]
    public bool Enabled { get; set; } 
    
    #endregion
    
    #region Model and Provider
    
    /// <summary>
    /// Gets or sets the model identifier string used by the provider's API.
    /// For example: "text-embedding-3-small", "text-embedding-004".
    /// </summary>
    [RConfigProperty("general_model-string")]
    public string ModelString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the provider (e.g., "openai", "gemini") that hosts this embedding model.
    /// </summary>
    [RConfigProperty("general_provider-name")]
    public string ProviderName { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the provider profile instance associated with this embedding model.
    /// This is populated during initialization based on the ProviderName.
    /// </summary>
    public ProviderProfile? Provider { get; set; } 
    
    #endregion
    
    #region Overall Options
    
    /// <summary>
    /// Gets or sets the tier classification of this embedding model (A, B, or C).
    /// Higher tiers generally indicate better performance or more expensive models.
    /// </summary>
    [RConfigProperty("settings_tier")]
    public ModelTier Tier { get; set; } 
    
    /// <summary>
    /// Gets or sets the maximum number of tokens that can be processed by this embedding model in a single request.
    /// </summary>
    [RConfigProperty("settings_token-limit")]
    public int TokenLimit { get; set; } 
    
    /// <summary>
    /// Gets or sets the type of token limit enforcement (Input, Output, or Combined).
    /// </summary>
    [RConfigProperty("settings_max-token-type")]
    public MaxTokenType? MaxTokenType { get; set; }
    
    #endregion
    
    #region Setting Overrides
    
    /// <summary>
    /// Gets or sets the override for maximum number of tokens to process.
    /// Can be a specific number or "disabled" to remove the limit.
    /// </summary>
    [RConfigProperty("override-settings_max-tokens")]
    public string? MaxTokens { get; set; }
    
    /// <summary>
    /// Gets or sets the override for request timeout duration.
    /// Can be a specific number of seconds or "disabled" to use provider defaults.
    /// </summary>
    [RConfigProperty("override-settings_timeout")]
    public string? Timeout { get; set; }
    
    /// <summary>
    /// Gets or sets the override for the number of retry attempts on failed requests.
    /// If null, uses the provider's default retry settings.
    /// </summary>
    [RConfigProperty("override-settings_retry-attempts")]
    public int? RetryAttempts { get; set; }
    
    #endregion
    
    #region Embedding-Specific Settings
    
    /// <summary>
    /// Gets or sets the number of dimensions for the output embedding vector.
    /// Some models support dimension reduction (e.g., OpenAI's text-embedding-3 models).
    /// Common values: 256, 512, 768, 1024, 1536, 3072.
    /// If null, uses the model's default dimension count.
    /// </summary>
    [RConfigProperty("embedding-settings_dimensions")]
    public int? Dimensions { get; set; }
    
    /// <summary>
    /// Gets or sets the encoding format for the returned embeddings.
    /// Common values: "float" (default), "base64".
    /// If null, uses the provider's default format (typically "float").
    /// </summary>
    [RConfigProperty("embedding-settings_encoding-format")]
    public string? EncodingFormat { get; set; }
    
    /// <summary>
    /// Gets or sets the task type for the embedding operation.
    /// Used by some models (like Gemini) to optimize embeddings for specific use cases.
    /// Common values: "retrieval_query", "retrieval_document", "semantic_similarity", "classification", "clustering".
    /// If null, uses the model's default task type.
    /// </summary>
    [RConfigProperty("embedding-settings_task-type")]
    public string? TaskType { get; set; }
    
    /// <summary>
    /// Gets or sets whether to return normalized embedding vectors (unit vectors with length 1).
    /// Normalized embeddings are useful for cosine similarity calculations.
    /// If null, uses the model's default normalization behavior.
    /// </summary>
    [RConfigProperty("embedding-settings_normalize")]
    public bool? NormalizeEmbeddings { get; set; }
    
    #endregion
    
    
    // ==============
    //  Constructors
    // ==============
    
    /// <summary>
    /// Initializes the embedding profile by resolving and validating the associated provider.
    /// This method is called automatically by the RConfigParser after the object is constructed.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when ProviderName is empty or null.</exception>
    public void Init()
    {
        // Validate that provider name is specified
        if (string.IsNullOrEmpty(ProviderName))
        {
            Enabled = false;
            throw new ArgumentNullException(nameof(ProviderName), "ProviderName is empty or null!");
        }

        // Attempt to find the provider
        var foundProvider = ProviderManager.Get(ProviderName);
        if (foundProvider is null)
        {
            Enabled = false;
            Util.Log($"Embedding model '{Name}': Provider '{ProviderName}' could not be found");
            return;
        }
        
        // Check if provider is enabled
        if (foundProvider.Enabled is false)
        {
            Enabled = false;
            Util.Log($"Embedding model '{Name}': Provider '{ProviderName}' is not enabled");
            return;
        }

        // Store the provider reference
        Provider = foundProvider;
    }
}
