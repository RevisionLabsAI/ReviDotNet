// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Defines the geographic processing boundary required for routed requests.
/// </summary>
public enum DataResidencyPolicy
{
    /// <summary>Do not impose a geographic processing boundary.</summary>
    Any,

    /// <summary>Require processing to remain within the European Union.</summary>
    EU,

    /// <summary>Require processing to remain within the United States.</summary>
    US
}
