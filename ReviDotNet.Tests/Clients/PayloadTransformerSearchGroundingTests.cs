// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Tests for Gemini search-grounding tool emission. Gemini 2.x models reject the legacy
/// <c>googleSearchRetrieval</c> tool with 400 INVALID_ARGUMENT ("google_search_retrieval is not
/// supported. Please use google_search tool instead."); the transformer must emit the modern
/// <c>googleSearch</c> tool.
/// </summary>
public class PayloadTransformerSearchGroundingTests
{
    /// <summary>Builds a Gemini-protocol transformer.</summary>
    /// <returns>The configured transformer.</returns>
    private static PayloadTransformer Transformer() => new(new InferClientConfig
    {
        ApiUrl = "https://api.example.com/",
        ApiKey = "test",
        Protocol = Protocol.Gemini,
        DefaultModel = "m"
    });

    [Fact]
    public void Gemini_SearchGrounding_EmitsGoogleSearchTool()
    {
        Dictionary<string, object> payload = new()
        {
            ["prompt"] = "hi",
            ["use_search_grounding"] = true
        };

        Dictionary<string, object> gemini = Transformer().TransformToGeminiPayload(payload);

        gemini.Should().ContainKey("tools");
        string json = JsonConvert.SerializeObject(gemini["tools"]);
        json.Should().Contain("\"googleSearch\":");
        json.Should().NotContain("googleSearchRetrieval", "Gemini 2.x rejects the legacy retrieval tool with 400 INVALID_ARGUMENT");
    }

    [Fact]
    public void Gemini_SearchGroundingDisabled_EmitsNoTools()
    {
        Dictionary<string, object> payload = new()
        {
            ["prompt"] = "hi",
            ["use_search_grounding"] = false
        };

        Dictionary<string, object> gemini = Transformer().TransformToGeminiPayload(payload);

        gemini.Should().NotContainKey("tools");
    }
}
