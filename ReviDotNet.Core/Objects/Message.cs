// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;

namespace Revi;

public class Message
{
    [JsonProperty("role")]
    public string Role;
    
    [JsonProperty("content")]
    public string Content;

    public Message(string role, string content)
    {
        Role = role;
        Content = content;
    }
}