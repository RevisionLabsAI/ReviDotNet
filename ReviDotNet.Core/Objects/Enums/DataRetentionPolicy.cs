// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Defines a required upper bound on upstream prompt and response retention.
/// </summary>
public enum DataRetentionPolicy
{
    /// <summary>Require an upstream that retains no prompt or response data at rest.</summary>
    None
}
