// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// DI interface for LLM inference operations. Replaces the static <c>Infer</c> class.
/// Inject as <c>IInferService infer</c> for clean call sites: <c>infer.ToObject&lt;T&gt;(...)</c>.
/// </summary>
public interface IInferService
{
    // ── Completion ──────────────────────────────────────────────────────

    /// <summary>Runs a completion for a prompt object with full parameter control.</summary>
    Task<CompletionResult?> Completion(
        Prompt prompt,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        Type? outputType = null,
        CancellationToken token = default,
        bool directRoute = false);

    /// <summary>Runs a completion for a named prompt.</summary>
    Task<CompletionResult?> Completion(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        Type? outputType = null,
        CancellationToken token = default);

    /// <summary>Streams a completion for a prompt object.</summary>
    IAsyncEnumerable<string> CompletionStream(
        Prompt prompt,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        Type? outputType = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool directRoute = false);

    // ── Type converters ─────────────────────────────────────────────────

    /// <summary>Runs a prompt and deserializes the result to <typeparamref name="T"/>.</summary>
    Task<T?> ToObject<T>(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int retryAttempt = 0,
        int? originalRetryLimit = null,
        CancellationToken token = default);

    /// <summary>Single-input convenience overload for <see cref="ToObject{T}(string,List{Input}?,ModelProfile?,string?,int,int?,CancellationToken)"/>.</summary>
    Task<T?> ToObject<T>(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>Runs a prompt and parses the result as an enum value.</summary>
    Task<TEnum> ToEnum<TEnum>(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        bool includeEnumValues = false,
        int retryAttempt = 0,
        int? originalRetryLimit = null,
        CancellationToken token = default) where TEnum : struct, Enum;

    /// <summary>Single-input convenience overload for <see cref="ToEnum{TEnum}(string,List{Input}?,ModelProfile?,string?,bool,int,int?,CancellationToken)"/>.</summary>
    Task<TEnum> ToEnum<TEnum>(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        bool includeEnumValues = false,
        CancellationToken token = default) where TEnum : struct, Enum;

    /// <summary>Runs a prompt and returns the raw selected text.</summary>
    Task<string?> ToString(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>Single-input convenience overload for <see cref="ToString(string,List{Input}?,ModelProfile?,string?,CancellationToken)"/>.</summary>
    Task<string?> ToString(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>Runs a prompt and parses the result as a boolean.</summary>
    Task<bool?> ToBool(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>Single-input convenience overload for <see cref="ToBool(string,List{Input}?,ModelProfile?,string?,CancellationToken)"/>.</summary>
    Task<bool?> ToBool(
        string promptName,
        Input? input = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>Runs a prompt and parses the result as a <see cref="JObject"/>.</summary>
    Task<JObject?> ToJObject(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>Single-input convenience overload for <see cref="ToJObject(string,List{Input}?,ModelProfile?,string?,CancellationToken)"/>.</summary>
    Task<JObject?> ToJObject(
        string promptName,
        Input? input = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>Runs a prompt and splits the result into a list of lines.</summary>
    Task<List<string>> ToStringList(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int retryAttempt = 0,
        int? originalRetryLimit = null,
        CancellationToken token = default);

    /// <summary>Single-input convenience overload for <see cref="ToStringList(string,List{Input}?,ModelProfile?,string?,int,int?,CancellationToken)"/>.</summary>
    Task<List<string>> ToStringList(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default);

    /// <summary>
    /// Streams a completion and returns lines; optionally stops early when
    /// <paramref name="maxLines"/> is reached or <paramref name="evaluator"/> returns true.
    /// </summary>
    Task<List<string>> ToStringListLimited(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int? maxLines = null,
        Func<string, bool>? evaluator = null,
        CancellationToken token = default);

    /// <summary>Single-input convenience overload for <see cref="ToStringListLimited(string,List{Input}?,ModelProfile?,string?,int?,Func{string,bool}?,CancellationToken)"/>.</summary>
    Task<List<string>> ToStringListLimited(
        string promptName,
        Input? input = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int? maxLines = null,
        Func<string, bool>? evaluator = null,
        CancellationToken token = default);

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Resolves a named prompt; throws if not found.</summary>
    Prompt FindPrompt(string name);

    /// <summary>Formats a list of inputs for a given model template.</summary>
    string? ListInputs(ModelProfile model, List<Input>? inputs);
}
