// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Util;

/// <summary>
/// The inference HTTP client logs the full request URL on non-success/retry responses, and the Gemini
/// protocol passes the API key as a <c>?key=&lt;API_KEY&gt;</c> query parameter. <see cref="Revi.Util.RedactSecrets"/>
/// must strip that secret (and Authorization / x-api-key header values) before anything reaches a log
/// sink, while keeping the rest of the URL intact for debugging.
/// </summary>
public class RedactSecretsTests
{
    [Fact]
    public void Masks_KeyQueryParameter_Value()
    {
        string redacted = Revi.Util.RedactSecrets("https://host/path?key=SECRET");

        redacted.Should().Be("https://host/path?key=***");
        redacted.Should().NotContain("SECRET");
    }

    [Fact]
    public void Redacts_ObservedGeminiRetryLogLine_KeepingTheRestIntact()
    {
        // The exact shape logged by InferenceHttpClient/StreamingProcessor on a retry.
        string line = "[1] Non-success response from " +
                      "'https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=AIzaSyA_REAL_LOOKING_KEY'" +
                      ", retrying in 5s: Not Found (404)";

        string redacted = Revi.Util.RedactSecrets(line);

        redacted.Should().NotContain("AIzaSyA_REAL_LOOKING_KEY", "the API key must never reach a log sink");
        redacted.Should().Contain("?key=***");
        // Everything else stays put so the line is still useful for debugging.
        redacted.Should().Contain("gemini-2.5-flash:generateContent");
        redacted.Should().Contain("retrying in 5s: Not Found (404)");
    }

    [Theory]
    [InlineData("https://host/path?key=SECRET", "https://host/path?key=***")]
    [InlineData("https://host/path?model=gemini-2.5-flash&key=SECRET&foo=bar",
                "https://host/path?model=gemini-2.5-flash&key=***&foo=bar")]
    [InlineData("https://host/path?api_key=SECRET", "https://host/path?api_key=***")]
    [InlineData("https://host/path?apikey=SECRET", "https://host/path?apikey=***")]
    [InlineData("https://host/path?access_token=SECRET", "https://host/path?access_token=***")]
    public void Masks_SensitiveQueryParameters_PreservingEverythingElse(string input, string expected)
    {
        Revi.Util.RedactSecrets(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://host/path")]
    [InlineData("https://host/path?model=gemini-2.5-flash&stream=true")]
    public void LeavesUrlsWithoutSecrets_Unchanged(string url)
    {
        Revi.Util.RedactSecrets(url).Should().Be(url);
    }

    [Fact]
    public void Scrubs_AuthorizationBearerHeader_IncludingTheToken()
    {
        string redacted = Revi.Util.RedactSecrets("Authorization: Bearer sk-live-abc123");

        redacted.Should().NotContain("sk-live-abc123");
        redacted.Should().Be("Authorization: ***");
    }

    [Fact]
    public void Scrubs_ApiKeyHeader_WithinALargerMessage()
    {
        string redacted = Revi.Util.RedactSecrets("request headers: x-api-key: AIzaSecretValue, accept: application/json");

        redacted.Should().NotContain("AIzaSecretValue");
        redacted.Should().Contain("x-api-key: ***");
        // A non-secret header sitting next to it survives.
        redacted.Should().Contain("accept: application/json");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Handles_NullAndEmpty_Safely(string? input, string expected)
    {
        Revi.Util.RedactSecrets(input).Should().Be(expected);
    }
}
