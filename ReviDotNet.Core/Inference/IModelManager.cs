// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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

public interface IModelManager
{
    /// <summary>
    /// Gets all available models.
    /// </summary>
    /// <returns>A list of all model profiles.</returns>
    List<ModelProfile> GetAll();

    ModelProfile? Get(string name);
    ModelProfile? Find(string? minTier, bool needsPromptCompletion = false);
    ModelProfile? Find(ModelTier? minTier, bool needsPromptCompletion = false);
}