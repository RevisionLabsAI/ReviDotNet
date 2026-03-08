// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// DI-backed facade for model lookups. Delegates to the legacy static ModelManager
/// to keep compatibility while enabling DI consumption throughout the app.
/// </summary>
public sealed class ModelRegistry : IModelManager
{
    public List<ModelProfile> GetAll() => ModelManager.GetAll();
    public ModelProfile? Get(string name) => ModelManager.Get(name);
    public ModelProfile? Find(string? minTier, bool needsPromptCompletion = false) => ModelManager.Find(minTier, needsPromptCompletion);
    public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false) => ModelManager.Find(minTier, needsPromptCompletion);
}