# Inference provider errors: what is retried, and what is not

`InferenceErrorClassifier` decides three separate things about a failed inference call, which is
why a single "retryable" boolean was not enough:

1. **Retry?** Only a transient fault or a genuine rate limit can succeed on a second attempt.
2. **Is the provider at fault?** A request we shaped wrongly says nothing about the provider's
   health, and must not cool it down.
3. **Does it need a person?** Exhausted credits, a rejected key and a permission problem do not
   clear on their own.

## Why this exists

Both retry loops used to key off the HTTP status alone, and `StreamingProcessor` did not even do
that — it retried **every** non-success status. On 2026-09-02 an OpenAI account with no credits
answered:

```json
{"error":{"message":"You have no credits remaining…","type":"insufficient_quota",
          "code":"credit_balance_exhausted"}}
```

with HTTP **429**. Every retry loop reads 429 as "slow down", so each hopeless call was retried five
times with exponential backoff — about 155 seconds — and name generation stalled behind it while
every dashboard still read healthy. The status code is not enough: the provider's own error code is
what separates "slow down" from "you cannot pay".

## The mapping

Only codes confirmed against provider documentation are mapped. Guessing at a code's meaning
produces confidently wrong retry behaviour, which is worse than the neutral `Unknown` an unmapped
code receives — the same rule `DomainProviderErrorClassifier` follows for registrars.

| Kind | Retry? | Blames provider? | Needs a person? | Signals |
|---|---|---|---|---|
| `Billing` | No | Yes | **Yes** | OpenAI-shaped 429 + `insufficient_quota`, `credit_balance_exhausted`, `organization_spend_limit_exceeded`, `project_spend_limit_exceeded`, `organization_usage_limit_exceeded`; Anthropic 402 `billing_error`; any status whose message names credits remaining/exhausted, insufficient quota, a spend or spending limit, billing details or a payment method (the bare word "billing" is not enough) |
| `Authentication` | No | Yes | **Yes** | 401; `invalid_api_key`, `authentication_error`, `invalid_organization` |
| `Permission` | No | Yes | **Yes** | 403; Anthropic `permission_error`; Gemini `PERMISSION_DENIED` |
| `RateLimited` | Yes | Yes | No | 429 with no billing code; Anthropic `rate_limit_error`; Gemini `RESOURCE_EXHAUSTED` |
| `Transient` | Yes | Yes | No | 408, 409, 498, 5xx; Anthropic `overloaded_error` (529) and `api_error`; network faults and timeouts |
| `RequestInvalid` | No | **No** | No | 400, 413, 422 and any other 4xx not listed above (405, 410, 415...); `json_validate_failed`, `context_length_exceeded`, `invalid_prompt`, `unsupported_parameter`; Anthropic `invalid_request_error`, `request_too_large` |
| `ModelUnavailable` | No | **No** | No | 404; `model_not_found`, `model_decommissioned`; Anthropic `not_found_error` |

**Providers covered.** The OpenAI error envelope is shared by every OpenAI-protocol provider we
carry — OpenAI, Groq, Kimi/Moonshot, Z.ai/GLM and self-hosted vLLM — so those are matched on code
rather than on host. Anthropic and Gemini have their own shapes and are matched on
`error.type` and `error.status` respectively.

**Order matters.** The provider's code is consulted *before* the HTTP status, because a billing
failure arrives as 429 on every OpenAI-protocol provider and reading the status first would misread
it as a rate limit — the exact bug this class removes.

**Two sharp edges.**

- Anthropic answers **400** when an organization spend limit is reached, which is otherwise the
  status for a malformed request. Only the message separates them, so the message is consulted
  rather than assumed either way.
- A 429 carrying no code at all falls through to the billing heuristic before being accepted as a
  rate limit, because a retried billing failure is the most expensive mistake here.

## Reporting

Every classified outcome is reported to `InferenceProviderMonitor`, a static event the host
subscribes to. The library deliberately stops there: taking a provider out of rotation, alerting
someone and drawing a dashboard are product decisions. In BetterNamer, `InferenceProviderHealthListener`
subscribes and feeds `IInferenceProviderHealthTracker`; see `/admin/inference-providers`.

A failure that cannot be retried is thrown as `InferenceProviderException`, which carries the
`InferenceFailure` so a caller can branch on the kind without parsing a message.

## Cancellation

A cancelled stream **throws** `OperationCanceledException` out of the enumeration (since
2026-09-04). It used to end quietly, which made "the caller stopped this" indistinguishable from
"the model had nothing to say": a consumer with a model fallback moved on to the next model with a
dead token. The `StreamingMetadata` completion still records the cancellation rather than an error,
and the error callback is not raised for it. A dropped connection (`HttpIOException`) still ends the
stream quietly, because what was received before the drop is real output.

## Retry-After

Both loops honour the provider's `Retry-After` header when it sends one, capped at 120 seconds so a
mis-set header cannot park a request. A rate limit is the provider telling us how long to wait, and
guessing shorter just earns another 429.

## Model attribution and a known gap

Each outcome names the model the request body carried (`payload["model"]`), falling back to the
provider's default model only when the body has none (Gemini puts the model in the URL).

**Errors inside the stream.** A provider that accepted a streaming request can still fail it
after the 200 — an overloaded backend, a quota that ran out between the connection and the first
token — and reports that as an error object on a data line (`data: {"error": {...}}`, or
Anthropic's `{"type": "error", ...}` event). `StreamingProcessor` classifies that payload exactly
as it would a failed connection, reports it to the monitor, and throws `InferenceProviderException`.
Before 2026-09-03 such a line parsed as a chunk with no text and was dropped, so the stream simply
ended empty and nothing was recorded.

## A related failure that is not the provider's: examples that contradict the schema

Groq answered `search-safety-classify` with 400 `json_validate_failed` on the same day. That was
classified `RequestInvalid` (not retried, provider not blamed) — correctly, because the request
really was wrong: the prompt's few-shot examples were written under a type-name root
(`{"SearchSafetyClassification": {...}}`) while the schema described the bare object, and the
model imitated the examples. See `prompt-files.md`, "The root of an example is the object, never
the type name", for the guard `ToObject<T>` now applies (`JsonOutputValidation.WarnIfExamplesDoNotConform`,
`TryUnwrapSingleRoot`).
