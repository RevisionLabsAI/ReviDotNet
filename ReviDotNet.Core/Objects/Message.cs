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