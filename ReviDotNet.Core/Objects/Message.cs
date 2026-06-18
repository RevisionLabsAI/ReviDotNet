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

    /// <summary>
    /// Optional inline images for vision-capable models. Null/omitted for ordinary text messages
    /// (back-compatible: serialized only when present). Currently produced by the file-reader tool
    /// when it sends an attached image to a vision model; the per-provider payload transformers
    /// (Gemini / Claude) translate these into the provider's image parts.
    /// </summary>
    [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)]
    public List<MessageImage>? Images;

    public Message(string role, string content)
    {
        Role = role;
        Content = content;
    }

    public Message(string role, string content, List<MessageImage>? images)
    {
        Role = role;
        Content = content;
        Images = images;
    }
}

/// <summary>A base64-encoded inline image attached to a <see cref="Message"/> for a vision model.</summary>
public sealed class MessageImage
{
    /// <summary>MIME type, e.g. <c>image/png</c> or <c>image/jpeg</c>.</summary>
    public string MediaType { get; }

    /// <summary>Base64-encoded image bytes (no data-URI prefix).</summary>
    public string Base64 { get; }

    public MessageImage(string mediaType, string base64)
    {
        MediaType = mediaType;
        Base64 = base64;
    }
}
