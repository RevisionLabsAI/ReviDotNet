// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;

namespace Revi;

/// <summary>
/// Turns a <see cref="GuidanceSchemaType"/> (a schema-sourcing <em>strategy</em>, e.g. json-auto vs
/// json-manual) into the concrete pieces the inference layer needs: the low-level
/// <see cref="GuidanceType"/> decode mode and the schema string.
/// <para>
/// The auto/manual distinction is purely about <em>where the schema string comes from</em>:
/// "auto" generates it from the call's output type; "manual" uses a supplied string (the prompt's
/// own schema, or — when resolving a provider-level default — the provider's default guidance string).
/// </para>
/// </summary>
public static class GuidanceResolver
{
    /// <summary>
    /// Reduces a schema strategy to the low-level decode mode used by the payload layer.
    /// The auto/manual distinction collapses here because it only governs schema sourcing, not decoding.
    /// Returns <c>null</c> for <see cref="GuidanceSchemaType.Default"/> / <c>null</c> (no standalone mode).
    /// </summary>
    public static GuidanceType? ReduceToGuidanceType(GuidanceSchemaType? schema) => schema switch
    {
        GuidanceSchemaType.Disabled => GuidanceType.Disabled,
        GuidanceSchemaType.JsonManual or GuidanceSchemaType.JsonAuto => GuidanceType.Json,
        GuidanceSchemaType.RegexManual or GuidanceSchemaType.RegexAuto => GuidanceType.Regex,
        GuidanceSchemaType.GNBFManual or GuidanceSchemaType.GNBFAuto => GuidanceType.Grammar,
        _ => null,
    };

    /// <summary>
    /// Resolves a concrete schema strategy into a (decode mode, schema string) pair.
    /// </summary>
    /// <param name="schema">
    /// The strategy to resolve. <see cref="GuidanceSchemaType.Default"/> is not a concrete strategy and
    /// is left unresolved (callers handle deferral before calling this); GNBF variants are not yet wired
    /// to a schema source and are likewise left unresolved.
    /// </param>
    /// <param name="manualSchema">Schema string used for <em>manual</em> strategies.</param>
    /// <param name="outputType">Target type used to generate the schema for <em>auto</em> strategies.</param>
    /// <param name="chainOfThought">Whether chain-of-thought is enabled (affects generated regex).</param>
    /// <param name="guidanceType">The resolved low-level decode mode (or <c>null</c> if unresolved).</param>
    /// <param name="guidanceString">The resolved schema string (or <c>null</c>).</param>
    public static void Resolve(
        GuidanceSchemaType schema,
        string? manualSchema,
        Type? outputType,
        bool chainOfThought,
        out GuidanceType? guidanceType,
        out string? guidanceString)
    {
        guidanceType = null;
        guidanceString = null;

        switch (schema)
        {
            case GuidanceSchemaType.Disabled:
                guidanceType = GuidanceType.Disabled;
                break;

            case GuidanceSchemaType.JsonManual:
                guidanceType = GuidanceType.Json;
                guidanceString = manualSchema;
                break;

            case GuidanceSchemaType.JsonAuto:
                guidanceType = GuidanceType.Json;
                guidanceString = Util.JsonStringFromType(outputType);
                break;

            case GuidanceSchemaType.RegexManual:
                guidanceType = GuidanceType.Regex;
                guidanceString = manualSchema;
                break;

            case GuidanceSchemaType.RegexAuto:
                guidanceType = GuidanceType.Regex;
                guidanceString = RegexGenerator.FromObject(outputType, chainOfThought, "<|eot_id|>");
                break;

            // GuidanceSchemaType.Default and the GNBF variants are intentionally left unresolved.
        }
    }
}
