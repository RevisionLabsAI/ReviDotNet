// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>DI interface for the inference model registry.</summary>
public interface IModelManager
{
    /// <summary>Loads model profiles from the application assembly.</summary>
    Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default);

    /// <summary>Returns the model profile with the given name, or null if not found.</summary>
    ModelProfile? Get(string name);

    /// <summary>Returns all loaded model profiles.</summary>
    List<ModelProfile> GetAll();

    /// <summary>Finds the lowest-tier enabled model meeting the minimum tier string.</summary>
    ModelProfile? Find(string? minTier, bool needsPromptCompletion = false);

    /// <summary>Finds the lowest-tier enabled model meeting the minimum tier string, excluding blocked models.</summary>
    ModelProfile? Find(string? minTier, bool needsPromptCompletion, List<string>? blockedModels);

    /// <summary>Finds the lowest-tier enabled model meeting the minimum tier enum.</summary>
    ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false);

    /// <summary>Finds the lowest-tier enabled model meeting the minimum tier enum, excluding blocked models.</summary>
    ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion, List<string>? blockedModels);

    /// <summary>Programmatically adds a model profile to the registry.</summary>
    void Add(ModelProfile model);
}
