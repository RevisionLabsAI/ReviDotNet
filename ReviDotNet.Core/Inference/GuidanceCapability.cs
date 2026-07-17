// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Which low-level <see cref="GuidanceType"/> decode modes each provider <see cref="Protocol"/> can
/// actually enforce, and a diagnostic that warns when a prompt's requested guidance strategy will be
/// silently dropped. Mirrors the per-protocol branches in <c>PayloadTransformer.AddOptionalParameters</c>.
/// </summary>
/// <remarks>
/// Capability matrix (also documented in provider-files.md):
/// <list type="bullet">
/// <item>OpenAI / Perplexity / Gemini / Claude — JSON schema only</item>
/// <item>vLLM — JSON and Regex</item>
/// <item>LLamaAPI — JSON and Grammar (GBNF)</item>
/// </list>
/// </remarks>
public static class GuidanceCapability
{
    /// <summary>
    /// Whether the given provider protocol can enforce the given decode mode on the wire. A null/Disabled
    /// mode is "nothing to enforce" and returns true. An unknown protocol returns false (conservative).
    /// </summary>
    public static bool Supports(Protocol? protocol, GuidanceType? type)
    {
        if (type is null or GuidanceType.Disabled)
            return true;

        return protocol switch
        {
            Protocol.OpenAI or Protocol.Perplexity or Protocol.Gemini or Protocol.Claude => type == GuidanceType.Json,
            Protocol.vLLM => type is GuidanceType.Json or GuidanceType.Regex,
            Protocol.LLamaAPI => type is GuidanceType.Json or GuidanceType.Grammar,
            _ => false,
        };
    }

    /// <summary>
    /// Warns (via <see cref="Util.Log"/>) when a prompt explicitly requested structured-output guidance
    /// but the request will produce no on-wire constraint — because the provider doesn't support guidance,
    /// the strategy resolved to an empty/unimplemented schema, or the provider protocol can't enforce the
    /// resolved decode mode. Stays silent for prompts that did not request guidance (null / Disabled), so
    /// it never fires for the agents' implicit JSON step contract or for plain text calls.
    /// </summary>
    /// <param name="prompt">The prompt being executed.</param>
    /// <param name="model">The selected model (supplies the provider/protocol).</param>
    /// <param name="resolvedType">The decode mode GetGuidance resolved (null if none).</param>
    /// <param name="resolvedString">The schema string GetGuidance resolved (null/empty if none).</param>
    /// <param name="effectiveSchema">
    /// The strategy that was actually applied after model-level overrides (a model rcfg's
    /// <c>guidance-schema-type</c> wins over the prompt's). Defaults to the prompt's strategy so a
    /// deliberate model-level disable does not warn as if guidance was silently dropped.
    /// </param>
    public static void WarnIfIneffective(
        Prompt prompt,
        ModelProfile model,
        GuidanceType? resolvedType,
        string? resolvedString,
        GuidanceSchemaType? effectiveSchema = null)
    {
        // Only when the author EXPLICITLY asked for guidance. Disabled is the intentional off-switch;
        // null means no guidance was requested at all.
        GuidanceSchemaType? schema = effectiveSchema ?? prompt.GuidanceSchema;
        if (schema is null or GuidanceSchemaType.Disabled)
            return;

        string strategy = schema.ToString()!;
        string provider = model.Provider?.Name ?? "unknown";

        // 1. Provider can't do guidance at all.
        if (!(model.Provider?.SupportsGuidance ?? false))
        {
            Util.Log($"Warning: prompt '{prompt.Name}' requests guidance ({strategy}) but provider '{provider}' " +
                     "does not support guidance — no output constraint will be applied.");
            return;
        }

        // 2. Strategy resolved to nothing: GNBF variants (unimplemented), `defer` with no provider default,
        //    or a *-manual strategy whose [[_schema]] was empty.
        bool needsSchema = resolvedType is GuidanceType.Json or GuidanceType.Regex or GuidanceType.Grammar;
        if (resolvedType is null or GuidanceType.Disabled
            || (needsSchema && string.IsNullOrWhiteSpace(resolvedString)))
        {
            Util.Log($"Warning: prompt '{prompt.Name}' guidance strategy '{strategy}' produced no constraints " +
                     "(empty schema or an unimplemented mode such as GNBF) — no output constraint will be applied.");
            return;
        }

        // 3. Provider supports guidance but not THIS decode mode (e.g. regex-auto against OpenAI).
        if (!Supports(model.Provider?.Protocol, resolvedType))
        {
            Util.Log($"Warning: prompt '{prompt.Name}' guidance mode '{resolvedType}' is not enforced by provider " +
                     $"'{provider}' (protocol {model.Provider?.Protocol}) — it will be silently ignored. " +
                     "See the guidance capability matrix in provider-files.md.");
        }
    }
}
