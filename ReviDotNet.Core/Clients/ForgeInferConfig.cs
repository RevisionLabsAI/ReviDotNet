// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

public class ForgeInferConfig
{
    public string ForgeUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 300;
}
