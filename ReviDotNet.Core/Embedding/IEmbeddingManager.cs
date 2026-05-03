// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>DI interface for the embedding model registry.</summary>
public interface IEmbeddingManager
{
    /// <summary>Loads embedding model profiles from the application assembly.</summary>
    Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default);

    /// <summary>Returns the embedding model profile with the given name, or null if not found.</summary>
    EmbeddingProfile? Get(string name);

    /// <summary>Returns all loaded embedding model profiles.</summary>
    IReadOnlyList<EmbeddingProfile> GetAll();

    /// <summary>Returns all enabled embedding model profiles.</summary>
    List<EmbeddingProfile> GetAllEnabled();

    /// <summary>Finds the lowest-tier enabled model that meets or exceeds the minimum tier.</summary>
    EmbeddingProfile? Find(string? minTier);

    /// <summary>Finds the lowest-tier enabled model, excluding blocked model names.</summary>
    EmbeddingProfile? Find(string? minTier, List<string>? blockedModels);

    /// <summary>Finds the lowest-tier enabled model that meets or exceeds the minimum tier.</summary>
    EmbeddingProfile? Find(ModelTier? minTier);

    /// <summary>Finds the lowest-tier enabled model, excluding blocked model names.</summary>
    EmbeddingProfile? Find(ModelTier? minTier, List<string>? blockedModels);

    /// <summary>Programmatically adds an embedding model profile to the registry.</summary>
    void Add(EmbeddingProfile embeddingModel);
}
