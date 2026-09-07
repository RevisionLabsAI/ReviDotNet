// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Defines whether gateway routing may use upstreams that collect request data.
/// </summary>
public enum DataCollectionPolicy
{
    /// <summary>Allow upstreams that collect request data, subject to account-level controls.</summary>
    Allow,

    /// <summary>Exclude upstreams that collect request data.</summary>
    Deny
}
