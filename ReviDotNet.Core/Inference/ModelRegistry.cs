// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

namespace Revi;

/// <summary>
/// DI-backed facade for model lookups. Delegates to the legacy static ModelManager
/// to keep compatibility while enabling DI consumption throughout the app.
/// </summary>
public sealed class ModelRegistry : IModelManager
{
    public ModelProfile? Get(string name) => ModelManager.Get(name);
    public ModelProfile? Find(string? minTier, bool needsPromptCompletion = false) => ModelManager.Find(minTier, needsPromptCompletion);
    public ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false) => ModelManager.Find(minTier, needsPromptCompletion);
}