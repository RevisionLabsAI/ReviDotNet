using System.Net;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// What a failed inference call means, and therefore what the caller should do about it. The
/// classification decides three separate things, which is why a single "retryable" boolean was not
/// enough: whether to retry, whether the provider itself is at fault, and whether a human has to
/// intervene before the provider can work again.
/// </summary>
public enum InferenceFailureKind
{
    /// <summary>The failure could not be classified. Treated as transient, but not blamed on the provider.</summary>
    Unknown = 0,

    /// <summary>
    /// A timeout, a network fault or a 5xx. The identical request may well succeed on a retry, so
    /// the backoff loop is worth spending.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// A genuine rate limit: the provider is healthy and is asking us to slow down. Retryable, and
    /// the <c>Retry-After</c> header (when present) is the provider telling us how long to wait.
    /// </summary>
    RateLimited = 2,

    /// <summary>
    /// The request as sent is wrong — a malformed body, a schema the model could not satisfy, a
    /// context overflow. Repeating it unaltered fails identically, so it must not be retried; and
    /// because our request was at fault, it must not count against the provider's health either.
    /// </summary>
    RequestInvalid = 3,

    /// <summary>
    /// The credentials were rejected: missing, malformed, revoked or expired. Not retryable, and
    /// the provider cannot serve anything until someone fixes the key.
    /// </summary>
    Authentication = 4,

    /// <summary>
    /// The account cannot pay: credits exhausted, a spend cap reached, a billing problem. Not
    /// retryable — this is the case that made the whole classifier necessary, because providers
    /// report it as HTTP 429, which every naive retry loop reads as "slow down and try again".
    /// </summary>
    Billing = 5,

    /// <summary>
    /// The key is valid but not allowed to do this: an unsupported region, a workspace restriction,
    /// a disabled capability. Not retryable, and needs a human.
    /// </summary>
    Permission = 6,

    /// <summary>
    /// The named model does not exist, was decommissioned, or is not available to this account. Not
    /// retryable; the provider may be perfectly healthy for other models.
    /// </summary>
    ModelUnavailable = 7
}

/// <summary>
/// Classifies a failed inference HTTP response into an <see cref="InferenceFailureKind"/>, so the
/// retry loops stop burning their budget on failures that cannot succeed and the host can flag a
/// provider that needs attention.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Both retry loops used to key off the HTTP status alone, and the
/// streaming loop did not even do that — it retried every non-success status. An OpenAI account
/// with no credits answers <c>429</c> with <c>{"code":"credit_balance_exhausted"}</c>; five retries
/// with exponential backoff then cost about 155 seconds per call and could never succeed. The
/// status code is not enough: the provider's own error code is what separates "slow down" from
/// "you cannot pay".
/// </para>
/// <para>
/// <b>Only documented codes are mapped.</b> Guessing at a code's meaning produces confidently wrong
/// retry behaviour, which is worse than the neutral <see cref="InferenceFailureKind.Unknown"/> an
/// unmapped code receives. Each list below cites where its values come from; anything not on a list
/// falls through to the HTTP-status heuristics, which are conservative by design.
/// </para>
/// </remarks>
public static class InferenceErrorClassifier
{
    /// <summary>
    /// OpenAI error codes that mean the account cannot pay. All arrive as HTTP 429.
    /// <list type="bullet">
    ///   <item><c>insufficient_quota</c> — the error <c>type</c> on an exhausted account.</item>
    ///   <item><c>credit_balance_exhausted</c> — "Your organization has no prepaid credits remaining."</item>
    ///   <item><c>organization_spend_limit_exceeded</c>, <c>project_spend_limit_exceeded</c> — a
    ///     configured spend cap was reached; only a human raising the cap clears it.</item>
    ///   <item><c>organization_usage_limit_exceeded</c> — the usage ceiling for the org.</item>
    /// </list>
    /// Source: OpenAI API error-codes guide. Shared by every OpenAI-protocol provider we carry
    /// (Groq, Kimi/Moonshot, Z.ai/GLM and self-hosted vLLM all speak the same error envelope),
    /// which is why they are matched on code rather than on host.
    /// </summary>
    private static readonly string[] OpenAiBillingCodes =
    [
        "insufficient_quota",
        "credit_balance_exhausted",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded",
        "billing_not_active",
        "account_deactivated"
    ];

    /// <summary>
    /// OpenAI-shaped codes for a rejected or missing key. These arrive as HTTP 401 and are also
    /// matched by status, so the list exists mainly to catch providers that answer 400 with one of
    /// these codes.
    /// </summary>
    private static readonly string[] OpenAiAuthenticationCodes =
    [
        "invalid_api_key",
        "authentication_error",
        "invalid_authentication",
        "invalid_organization"
    ];

    /// <summary>
    /// OpenAI-shaped codes for a request that cannot succeed as written. <c>json_validate_failed</c>
    /// is Groq's code for "the model's structured output did not match the schema you supplied" —
    /// observed in production on 2026-09-02, when every safety classification failed with it. It is
    /// request-shaped: the same prompt and schema fail identically every time, and the provider is
    /// healthy.
    /// </summary>
    private static readonly string[] OpenAiRequestInvalidCodes =
    [
        "json_validate_failed",
        "context_length_exceeded",
        "string_above_max_length",
        "invalid_prompt",
        "unsupported_value",
        "unsupported_parameter"
    ];

    /// <summary>Codes naming a model that this account cannot call.</summary>
    private static readonly string[] ModelUnavailableCodes =
    [
        "model_not_found",
        "model_decommissioned",
        "model_terminated"
    ];

    /// <summary>
    /// Anthropic error <c>type</c> values, which are carried on a documented one-per-status basis:
    /// 401 <c>authentication_error</c>, 402 <c>billing_error</c>, 403 <c>permission_error</c>,
    /// 413 <c>request_too_large</c>, 429 <c>rate_limit_error</c>, 529 <c>overloaded_error</c>.
    /// Source: Claude API errors documentation.
    /// </summary>
    private const string AnthropicBillingType = "billing_error";

    /// <summary>Gemini reports a canonical status name in <c>error.status</c> rather than a code.</summary>
    private const string GeminiResourceExhausted = "RESOURCE_EXHAUSTED";

    /// <summary>Gemini's status for a key that is rejected or lacks access.</summary>
    private const string GeminiPermissionDenied = "PERMISSION_DENIED";

    /// <summary>
    /// Classifies a non-success inference response.
    /// </summary>
    /// <param name="statusCode">The HTTP status the provider returned.</param>
    /// <param name="responseBody">The raw response body, which carries the provider's own error code.</param>
    /// <returns>The classification, with the provider's error code when one could be parsed.</returns>
    public static InferenceFailure Classify(HttpStatusCode statusCode, string? responseBody)
    {
        (string? code, string? type, string? status, string? message) = ParseError(responseBody);

        // The provider's own code first. A billing failure arrives as 429 on every OpenAI-protocol
        // provider, so consulting the status before the code would misread it as a rate limit —
        // exactly the bug this class was written to remove.
        string? codeOrType = code ?? type;
        if (Matches(codeOrType, OpenAiBillingCodes) || Matches(type, OpenAiBillingCodes)
            || string.Equals(type, AnthropicBillingType, StringComparison.OrdinalIgnoreCase))
        {
            return new InferenceFailure(InferenceFailureKind.Billing, (int)statusCode, codeOrType, message);
        }

        if (Matches(codeOrType, OpenAiAuthenticationCodes))
        {
            return new InferenceFailure(InferenceFailureKind.Authentication, (int)statusCode, codeOrType, message);
        }

        if (Matches(codeOrType, ModelUnavailableCodes))
        {
            return new InferenceFailure(InferenceFailureKind.ModelUnavailable, (int)statusCode, codeOrType, message);
        }

        if (Matches(codeOrType, OpenAiRequestInvalidCodes))
        {
            return new InferenceFailure(InferenceFailureKind.RequestInvalid, (int)statusCode, codeOrType, message);
        }

        // Gemini names a canonical status rather than a code.
        if (string.Equals(status, GeminiPermissionDenied, StringComparison.OrdinalIgnoreCase))
        {
            return new InferenceFailure(InferenceFailureKind.Permission, (int)statusCode, status, message);
        }

        if (string.Equals(status, GeminiResourceExhausted, StringComparison.OrdinalIgnoreCase))
        {
            return new InferenceFailure(InferenceFailureKind.RateLimited, (int)statusCode, status, message);
        }

        // Then the status, which is authoritative for the cases no code disambiguates.
        InferenceFailureKind kind = (int)statusCode switch
        {
            401 => InferenceFailureKind.Authentication,
            402 => InferenceFailureKind.Billing,
            403 => InferenceFailureKind.Permission,
            404 => InferenceFailureKind.ModelUnavailable,
            408 => InferenceFailureKind.Transient,
            409 => InferenceFailureKind.Transient,
            413 or 422 => InferenceFailureKind.RequestInvalid,
            429 => InferenceFailureKind.RateLimited,
            // 400 is the awkward one: it is usually our request, but Anthropic also answers 400 when
            // an organization spend limit is reached. Only the message separates them, so the
            // message is consulted rather than assumed either way.
            400 => LooksLikeBilling(message) ? InferenceFailureKind.Billing : InferenceFailureKind.RequestInvalid,
            498 => InferenceFailureKind.Transient,
            >= 500 => InferenceFailureKind.Transient,
            // Any other 4xx (405, 410, 415, ...) is deterministic: the identical request fails
            // identically, so it is not worth the retry budget; and it says nothing about the
            // provider's health. Only a status outside 4xx/5xx is genuinely unknown.
            >= 400 => InferenceFailureKind.RequestInvalid,
            _ => InferenceFailureKind.Unknown
        };

        // A last look at the message for providers that answer with prose rather than a code. Only
        // consulted when nothing better classified the failure, and only for billing, because that
        // is the kind whose cost of being retried is highest and whose wording is most consistent.
        if (kind is InferenceFailureKind.RateLimited or InferenceFailureKind.Unknown && LooksLikeBilling(message))
        {
            kind = InferenceFailureKind.Billing;
        }

        return new InferenceFailure(kind, (int)statusCode, codeOrType ?? status, message);
    }

    /// <summary>
    /// Whether a failure of this kind is worth retrying. Only transient faults and genuine rate
    /// limits are: everything else fails identically however many times it is sent.
    /// </summary>
    /// <param name="kind">The classification.</param>
    /// <returns><see langword="true"/> when a retry could succeed.</returns>
    public static bool IsRetryable(InferenceFailureKind kind)
        => kind is InferenceFailureKind.Transient or InferenceFailureKind.RateLimited or InferenceFailureKind.Unknown;

    /// <summary>
    /// Whether a failure of this kind means the provider cannot serve any request until a person
    /// intervenes — the signal a host uses to take the provider out of rotation and raise an alert.
    /// A rate limit deliberately does not qualify: it clears on its own.
    /// </summary>
    /// <param name="kind">The classification.</param>
    /// <returns><see langword="true"/> when the provider needs human attention.</returns>
    public static bool RequiresIntervention(InferenceFailureKind kind)
        => kind is InferenceFailureKind.Billing or InferenceFailureKind.Authentication or InferenceFailureKind.Permission;

    /// <summary>
    /// Whether the failure says anything about the provider's health at all. A request we shaped
    /// wrongly does not: the provider answered correctly and told us so, and counting it would cool
    /// down a healthy provider for a defect on our side.
    /// </summary>
    /// <param name="kind">The classification.</param>
    /// <returns><see langword="true"/> when the failure should count against the provider.</returns>
    public static bool ReflectsProviderHealth(InferenceFailureKind kind)
        => kind is not (InferenceFailureKind.RequestInvalid or InferenceFailureKind.ModelUnavailable);

    /// <summary>Whether a value appears in a code list, case-insensitively.</summary>
    /// <param name="value">The parsed code or type.</param>
    /// <param name="codes">The list to match against.</param>
    /// <returns><see langword="true"/> on a match.</returns>
    private static bool Matches(string? value, string[] codes)
        => !string.IsNullOrWhiteSpace(value) && codes.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a message reads like a billing or quota problem. The wording providers use is
    /// remarkably consistent ("credits", "quota", "billing", "spend limit"), and this is only ever a
    /// fallback for responses that carried no usable code.
    /// </summary>
    /// <param name="message">The provider's message.</param>
    /// <returns><see langword="true"/> when it looks like a payment problem.</returns>
    private static bool LooksLikeBilling(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        string text = message.ToLowerInvariant();
        // Phrases, not the bare word "billing": a 400 whose message merely mentions a billing
        // address must not take a provider out of service.
        return text.Contains("credit") && (text.Contains("remaining") || text.Contains("balance") || text.Contains("low") || text.Contains("exhausted"))
            || text.Contains("insufficient quota")
            || text.Contains("insufficient_quota")
            || text.Contains("exceeded your current quota")
            || text.Contains("spend limit")
            || text.Contains("spending limit")
            || text.Contains("billing details")
            || text.Contains("billing information")
            || text.Contains("plan and billing")
            || text.Contains("payment method")
            || text.Contains("account is not active");
    }

    /// <summary>
    /// Pulls the error code, type, canonical status and message out of a provider response body.
    /// Tolerates every envelope we speak to: OpenAI-shaped (<c>error.code</c>/<c>error.type</c>),
    /// Anthropic (<c>error.type</c>) and Gemini (<c>error.status</c>).
    /// </summary>
    /// <param name="responseBody">The raw body; may be empty or not JSON at all.</param>
    /// <returns>The parsed parts, each null when absent.</returns>
    private static (string? Code, string? Type, string? Status, string? Message) ParseError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return (null, null, null, null);
        }

        try
        {
            JToken root = JToken.Parse(responseBody);
            JToken? error = root["error"] ?? root;
            return (
                (string?)error["code"],
                (string?)error["type"],
                (string?)error["status"],
                (string?)error["message"]);
        }
        catch (Exception)
        {
            // Not JSON, or not a shape we know: fall back to the raw text as the message so the
            // billing heuristic and the log line still have something to work with.
            return (null, null, null, responseBody);
        }
    }
}

/// <summary>
/// The result of classifying a failed inference response: what kind of failure it was, and the
/// provider's own words for it.
/// </summary>
/// <param name="Kind">The classification.</param>
/// <param name="StatusCode">The HTTP status the provider returned.</param>
/// <param name="ProviderCode">The provider's error code, type or canonical status, when one was parsed.</param>
/// <param name="Message">The provider's message, when one was parsed.</param>
public readonly record struct InferenceFailure(
    InferenceFailureKind Kind,
    int StatusCode,
    string? ProviderCode,
    string? Message)
{
    /// <summary>Whether retrying this failure could succeed.</summary>
    public bool IsRetryable => InferenceErrorClassifier.IsRetryable(Kind);

    /// <summary>Whether this failure needs a person before the provider can work again.</summary>
    public bool RequiresIntervention => InferenceErrorClassifier.RequiresIntervention(Kind);

    /// <summary>Whether this failure says anything about the provider's health.</summary>
    public bool ReflectsProviderHealth => InferenceErrorClassifier.ReflectsProviderHealth(Kind);

    /// <inheritdoc />
    public override string ToString()
        => ProviderCode is null ? $"{Kind} ({StatusCode})" : $"{Kind} ({StatusCode} {ProviderCode})";
}
