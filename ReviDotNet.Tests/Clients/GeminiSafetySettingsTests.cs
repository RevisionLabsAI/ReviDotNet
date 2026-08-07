// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Gemini's <c>safetySettings</c>, which let a caller set the harm-block threshold per harm category.
/// </summary>
/// <remarks>
/// Needed because Gemini's defaults are actively wrong for one job: classifying content for safety.
/// Asked to categorise a description of harmful activity, Gemini suppresses its own answer — returning
/// null category/confidence/rationale, or omitting the fields altogether — and a caller that treats an
/// unparseable verdict as "allowed" then does the opposite of what it intended, most reliably on the
/// very worst input. Moderation is the use case Google exposes this field for.
/// <para>
/// Every assertion here is about payload SHAPE, because all three ways to get this wrong compile
/// cleanly: putting the field inside <c>generationConfig</c> (Gemini rejects it), omitting categories
/// (Gemini silently keeps its default for each one missing), and swapping this value with
/// <c>thinking</c> in the positional relay through <c>InferClient</c> — both are <c>string?</c>, so
/// the compiler cannot tell them apart. The last one happened while writing this feature.
/// </para>
/// </remarks>
public class GeminiSafetySettingsTests
{
    /// <summary>The harm categories Gemini is asked about, deliberately excluding CIVIC_INTEGRITY.</summary>
    private static readonly string[] ExpectedCategories =
    [
        "HARM_CATEGORY_HARASSMENT",
        "HARM_CATEGORY_HATE_SPEECH",
        "HARM_CATEGORY_SEXUALLY_EXPLICIT",
        "HARM_CATEGORY_DANGEROUS_CONTENT"
    ];

    /// <summary>Builds a Gemini payload from the given loose parameters.</summary>
    /// <param name="payload">The pre-transform parameter bag.</param>
    /// <returns>The transformed Gemini payload.</returns>
    private static Dictionary<string, object> Transform(Dictionary<string, object> payload)
        => new PayloadTransformer(new InferClientConfig
        {
            ApiUrl = "https://generativelanguage.googleapis.com/v1beta/",
            ApiKey = "test",
            Protocol = Protocol.Gemini,
            DefaultModel = "gemini-2.5-flash",
            SupportsGuidance = true
        }).TransformToGeminiPayload(payload);

    /// <summary>
    /// A configured threshold reaches the payload as a top-level <c>safetySettings</c> array covering
    /// every harm category.
    /// </summary>
    [Fact]
    public void A_configured_threshold_is_emitted_for_every_category()
    {
        Dictionary<string, object> gemini = Transform(new Dictionary<string, object>
        {
            ["gemini_safety_threshold"] = "BLOCK_NONE"
        });

        gemini.Should().ContainKey("safetySettings");

        string json = JsonConvert.SerializeObject(gemini["safetySettings"]);
        List<Dictionary<string, string>> settings =
            JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json)!;

        settings.Select(s => s["category"]).Should().BeEquivalentTo(ExpectedCategories,
            "Gemini keeps its default for any harm category the caller leaves out, so a partial list "
            + "silently half-applies");
        settings.Should().OnlyContain(s => s["threshold"] == "BLOCK_NONE");
    }

    /// <summary>
    /// It is a TOP-LEVEL field. Gemini rejects <c>safetySettings</c> nested inside
    /// <c>generationConfig</c>, and nesting it compiles perfectly well.
    /// </summary>
    [Fact]
    public void It_sits_beside_generationConfig_not_inside_it()
    {
        Dictionary<string, object> gemini = Transform(new Dictionary<string, object>
        {
            ["gemini_safety_threshold"] = "OFF",
            ["temperature"] = 0.5f
        });

        gemini.Should().ContainKey("safetySettings");
        gemini.Should().ContainKey("generationConfig");
        ((Dictionary<string, object>)gemini["generationConfig"])
            .Should().NotContainKey("safetySettings");
    }

    /// <summary>
    /// Absent configuration emits nothing at all, leaving Gemini's defaults in place. Ordinary prompts
    /// must not have their filtering silently relaxed by this feature existing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_threshold_emits_no_safetySettings(string? threshold)
    {
        Dictionary<string, object> payload = new();
        if (threshold != null)
            payload["gemini_safety_threshold"] = threshold;

        Transform(payload).Should().NotContainKey("safetySettings",
            "an unconfigured prompt must keep Gemini's own defaults");
    }

    /// <summary>The value is normalised to the upper-case form the API expects.</summary>
    /// <param name="configured">What the config file said.</param>
    [Theory]
    [InlineData("block_none")]
    [InlineData("  Block_None  ")]
    public void The_threshold_is_normalised(string configured)
    {
        Dictionary<string, object> gemini = Transform(new Dictionary<string, object>
        {
            ["gemini_safety_threshold"] = configured
        });

        string json = JsonConvert.SerializeObject(gemini["safetySettings"]);
        JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json)!
            .Should().OnlyContain(s => s["threshold"] == "BLOCK_NONE");
    }

    /// <summary>
    /// The safety threshold and the thinking budget are independent and must not be confused.
    /// </summary>
    /// <remarks>
    /// This is the regression guard for a real bug: <c>InferClient</c> relays these two POSITIONALLY,
    /// both are <c>string?</c>, and inserting the new parameter in the wrong slot swapped them while
    /// compiling without a murmur. A swap shows up here as a numeric thinking budget appearing as a
    /// harm threshold.
    /// </remarks>
    [Fact]
    public void Thinking_and_safety_threshold_do_not_get_swapped()
    {
        Dictionary<string, object> gemini = Transform(new Dictionary<string, object>
        {
            ["gemini_safety_threshold"] = "BLOCK_NONE",
            ["thinking_mode"] = "2048"
        });

        var generationConfig = (Dictionary<string, object>)gemini["generationConfig"];
        var thinkingConfig = (Dictionary<string, object>)generationConfig["thinkingConfig"];
        thinkingConfig["thinkingBudget"].Should().Be(2048);

        string json = JsonConvert.SerializeObject(gemini["safetySettings"]);
        JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json)!
            .Should().OnlyContain(s => s["threshold"] == "BLOCK_NONE");
    }
}
