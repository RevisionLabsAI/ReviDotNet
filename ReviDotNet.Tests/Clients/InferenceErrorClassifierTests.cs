// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// Pins what a failed inference response means: whether it is retried, whether the provider is
/// blamed, and whether a person has to intervene. The case that made the classifier necessary: an
/// OpenAI account with no credits answers HTTP 429 with code <c>credit_balance_exhausted</c>, and
/// a status-only retry loop read that as "slow down" — five attempts, about 155 seconds, and it
/// could never succeed.
/// </summary>
public sealed class InferenceErrorClassifierTests
{
    /// <summary>Exhausted credits arrive as 429 and must never be retried; the provider needs a person.</summary>
    [Fact]
    public void Exhausted_credits_are_billing_not_a_rate_limit()
    {
        const string body = """{"error":{"message":"You have no credits remaining. Add credits to continue using the API.","type":"insufficient_quota","param":null,"code":"credit_balance_exhausted"}}""";

        InferenceFailure failure = InferenceErrorClassifier.Classify(HttpStatusCode.TooManyRequests, body);

        failure.Kind.Should().Be(InferenceFailureKind.Billing);
        failure.IsRetryable.Should().BeFalse();
        failure.RequiresIntervention.Should().BeTrue();
        failure.ReflectsProviderHealth.Should().BeTrue();
        failure.ProviderCode.Should().Be("credit_balance_exhausted");
    }

    /// <summary>Every documented OpenAI billing code is terminal, whatever the status carries it.</summary>
    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("credit_balance_exhausted")]
    [InlineData("organization_spend_limit_exceeded")]
    [InlineData("project_spend_limit_exceeded")]
    [InlineData("organization_usage_limit_exceeded")]
    public void OpenAI_billing_codes_are_terminal(string code)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify(
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"...\",\"type\":\"x\",\"code\":\"" + code + "\"}}");

        failure.Kind.Should().Be(InferenceFailureKind.Billing);
        failure.IsRetryable.Should().BeFalse();
    }

    /// <summary>A 429 with no billing code is a genuine rate limit: retryable, and it clears itself.</summary>
    [Fact]
    public void A_plain_rate_limit_is_retryable_and_needs_nobody()
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify(
            HttpStatusCode.TooManyRequests,
            """{"error":{"message":"Rate limit reached for gpt-4o","type":"requests","code":"rate_limit_exceeded"}}""");

        failure.Kind.Should().Be(InferenceFailureKind.RateLimited);
        failure.IsRetryable.Should().BeTrue();
        failure.RequiresIntervention.Should().BeFalse();
        failure.ReflectsProviderHealth.Should().BeTrue("a rate limit is still the provider's state, and cools it down");
    }

    /// <summary>
    /// A schema the model could not satisfy is our request: not retried, and not the provider's
    /// fault. Groq reports it as 400 <c>json_validate_failed</c>.
    /// </summary>
    [Fact]
    public void A_schema_failure_is_our_request_and_blames_nobody()
    {
        const string body = """{"error":{"message":"Generated JSON does not match the expected schema.","type":"invalid_request_error","code":"json_validate_failed"}}""";

        InferenceFailure failure = InferenceErrorClassifier.Classify(HttpStatusCode.BadRequest, body);

        failure.Kind.Should().Be(InferenceFailureKind.RequestInvalid);
        failure.IsRetryable.Should().BeFalse();
        failure.ReflectsProviderHealth.Should().BeFalse();
        failure.RequiresIntervention.Should().BeFalse();
    }

    /// <summary>Anthropic's documented statuses map to the same vocabulary.</summary>
    [Theory]
    [InlineData(401, "authentication_error", InferenceFailureKind.Authentication, false)]
    [InlineData(402, "billing_error", InferenceFailureKind.Billing, false)]
    [InlineData(403, "permission_error", InferenceFailureKind.Permission, false)]
    [InlineData(413, "request_too_large", InferenceFailureKind.RequestInvalid, false)]
    [InlineData(429, "rate_limit_error", InferenceFailureKind.RateLimited, true)]
    [InlineData(500, "api_error", InferenceFailureKind.Transient, true)]
    [InlineData(529, "overloaded_error", InferenceFailureKind.Transient, true)]
    public void Anthropic_statuses_classify(int status, string type, InferenceFailureKind expected, bool retryable)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify(
            (HttpStatusCode)status,
            "{\"type\":\"error\",\"error\":{\"type\":\"" + type + "\",\"message\":\"...\"}}");

        failure.Kind.Should().Be(expected);
        failure.IsRetryable.Should().Be(retryable);
    }

    /// <summary>
    /// An Anthropic error event inside a stream arrives with the status already 200, so the
    /// error type alone has to carry the classification.
    /// </summary>
    [Theory]
    [InlineData("overloaded_error", InferenceFailureKind.Transient)]
    [InlineData("rate_limit_error", InferenceFailureKind.RateLimited)]
    [InlineData("authentication_error", InferenceFailureKind.Authentication)]
    [InlineData("invalid_request_error", InferenceFailureKind.RequestInvalid)]
    [InlineData("billing_error", InferenceFailureKind.Billing)]
    public void Anthropic_types_classify_without_a_telling_status(string type, InferenceFailureKind expected)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify(
            HttpStatusCode.OK,
            "{\"type\":\"error\",\"error\":{\"type\":\"" + type + "\",\"message\":\"...\"}}");

        failure.Kind.Should().Be(expected);
    }

    /// <summary>Gemini names a canonical status rather than a code.</summary>
    [Theory]
    [InlineData(403, "PERMISSION_DENIED", InferenceFailureKind.Permission, false)]
    [InlineData(429, "RESOURCE_EXHAUSTED", InferenceFailureKind.RateLimited, true)]
    public void Gemini_statuses_classify(int status, string canonical, InferenceFailureKind expected, bool retryable)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify(
            (HttpStatusCode)status,
            "{\"error\":{\"code\":" + status + ",\"message\":\"...\",\"status\":\"" + canonical + "\"}}");

        failure.Kind.Should().Be(expected);
        failure.IsRetryable.Should().Be(retryable);
    }

    /// <summary>
    /// Any other 4xx is deterministic and fails fast as our request (405, 410, 415); only a status
    /// outside 4xx/5xx is unknown. Retrying a 405 five times is the same waste as retrying a 400.
    /// </summary>
    [Theory]
    [InlineData(405)]
    [InlineData(410)]
    [InlineData(415)]
    public void Other_client_errors_fail_fast_without_blaming_the_provider(int status)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify((HttpStatusCode)status, "");

        failure.Kind.Should().Be(InferenceFailureKind.RequestInvalid);
        failure.IsRetryable.Should().BeFalse();
        failure.ReflectsProviderHealth.Should().BeFalse();
    }

    /// <summary>Transient faults stay retryable, and a body that is not JSON does not derail the classifier.</summary>
    [Theory]
    [InlineData(502, "<html>bad gateway</html>")]
    [InlineData(503, "")]
    [InlineData(408, null)]
    public void Transient_statuses_stay_retryable(int status, string? body)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify((HttpStatusCode)status, body);

        failure.Kind.Should().Be(InferenceFailureKind.Transient);
        failure.IsRetryable.Should().BeTrue();
    }

    /// <summary>
    /// A provider that answers in prose is still caught for billing — the most expensive failure
    /// to retry — but only on phrases that mean it, not on the word "billing" alone.
    /// </summary>
    [Theory]
    [InlineData(429, "You exceeded your current quota, please check your plan and billing details.", InferenceFailureKind.Billing)]
    [InlineData(400, "Your organization has reached its spend limit for this month.", InferenceFailureKind.Billing)]
    [InlineData(400, "The billing address on the request is malformed.", InferenceFailureKind.RequestInvalid)]
    public void Prose_is_read_for_billing_but_only_on_phrases_that_mean_it(int status, string message, InferenceFailureKind expected)
    {
        InferenceFailure failure = InferenceErrorClassifier.Classify((HttpStatusCode)status, message);

        failure.Kind.Should().Be(expected);
    }

    /// <summary>The provider monitor isolates a throwing subscriber from the caller and still reaches the others.</summary>
    [Fact]
    public void The_monitor_isolates_a_throwing_subscriber()
    {
        List<InferenceProviderOutcome> seen = [];
        Action<InferenceProviderOutcome> bad = _ => throw new InvalidOperationException("boom");
        Action<InferenceProviderOutcome> good = seen.Add;
        InferenceProviderMonitor.OutcomeObserved += bad;
        InferenceProviderMonitor.OutcomeObserved += good;
        try
        {
            Action report = () => InferenceProviderMonitor.ReportFailure(
                "OpenAI", "gpt-4o", new InferenceFailure(InferenceFailureKind.Billing, 429, "insufficient_quota", "no credits"), streaming: true);

            report.Should().NotThrow("an inference call must never fail because a health listener did");
            seen.Should().ContainSingle();
            seen[0].ProviderName.Should().Be("openai", "provider names are normalized to lowercase");
            seen[0].ModelName.Should().Be("gpt-4o");
            seen[0].Failure.Kind.Should().Be(InferenceFailureKind.Billing);
            seen[0].Streaming.Should().BeTrue();
        }
        finally
        {
            InferenceProviderMonitor.OutcomeObserved -= bad;
            InferenceProviderMonitor.OutcomeObserved -= good;
        }
    }
}
