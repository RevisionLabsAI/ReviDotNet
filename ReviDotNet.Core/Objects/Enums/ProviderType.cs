// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Runtime.InteropServices.ObjectiveC;
using Newtonsoft.Json;
using Revi;


namespace Revi;

public enum ProviderType
{
    OpenAI,
    Groq,
    Custom
}