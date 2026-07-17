// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Json.Schema;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// Client-side JSON-schema validation for structured outputs. On-wire enforcement varies by
/// provider — strict constrained decoding (OpenAI, Kimi, Groq gpt-oss, vLLM, Gemini, Claude)
/// guarantees conformance, but the json-object downgrade (GLM) and guidance-disabled models only
/// promise syntactically valid JSON. This is the backstop: outputs are checked against the same
/// schema the guidance layer generated, and violations feed the existing ToObject retry path.
/// (Replaces the long-dead <c>ValidateToSchema</c> TODO in Infer.cs.)
/// </summary>
public static class JsonOutputValidation
{
    /// <summary>Per-type cache of prepared schemas — generation and lenient rewriting are not free.</summary>
    private static readonly ConcurrentDictionary<Type, JsonSchema?> _schemaCache = new();

    /// <summary>
    /// Validates a model's JSON output against the schema generated for the expected output type.
    /// Lenient on extra properties (additionalProperties is stripped before evaluation — extras are
    /// harmless to deserialization and models on unconstrained paths add them freely), strict on
    /// types, required fields, and enums. Null/empty JSON and schema-generation failures return
    /// true — the caller's null-object handling covers those; this check must never be the thing
    /// that breaks an otherwise-working flow.
    /// </summary>
    /// <param name="json">The extracted JSON output from the model.</param>
    /// <param name="outputType">The C# type the output will be deserialized into.</param>
    /// <param name="promptName">The prompt name, for log messages.</param>
    /// <returns>True when the output conforms (or nothing could be validated), false on violations.</returns>
    public static bool ConformsToType(string? json, Type? outputType, string promptName)
    {
        if (string.IsNullOrWhiteSpace(json) || outputType is null)
            return true;

        JsonSchema? schema = _schemaCache.GetOrAdd(outputType, BuildLenientSchema);
        if (schema is null)
            return true;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            // Not parseable as JSON at all — the deserialization/remediation path already handles this.
            return true;
        }

        EvaluationResults results = schema.Evaluate(node, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (results.IsValid)
            return true;

        List<string> errors = [];
        foreach (EvaluationResults detail in results.Details)
        {
            if (detail.HasErrors && detail.Errors is not null)
            {
                foreach (KeyValuePair<string, string> error in detail.Errors)
                    errors.Add($"{detail.InstanceLocation}: {error.Value}");
            }
        }

        Util.Log($"Schema validation FAILED for prompt '{promptName}' " +
                 $"({errors.Count} violation(s)): {string.Join("; ", errors.Take(10))}");
        return false;
    }

    /// <summary>
    /// Generates the output type's schema and strips every <c>additionalProperties</c> keyword so
    /// extra fields don't fail validation. Returns null when the schema can't be generated or
    /// parsed (validation is then skipped for that type).
    /// </summary>
    /// <param name="outputType">The output type to build a schema for.</param>
    /// <returns>The prepared schema, or null.</returns>
    private static JsonSchema? BuildLenientSchema(Type outputType)
    {
        try
        {
            // Run the raw generated schema through the same strict-mode processing the wire path
            // uses (required-all, object root) so the backstop validates the contract providers
            // were asked to enforce — then relax additionalProperties for the leniency described above.
            string schemaString = Util.JsonStringFromType(outputType);
            object processed = Util.AddAdditionalPropertiesToSchema(schemaString);
            JToken token = JToken.FromObject(processed);
            StripAdditionalProperties(token);
            return JsonSchema.FromText(token.ToString());
        }
        catch (Exception e)
        {
            Util.Log($"JsonOutputValidation: could not build schema for type '{outputType.Name}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Recursively removes <c>additionalProperties</c> from every object node in the schema tree.
    /// </summary>
    /// <param name="token">The schema (sub)tree to clean in place.</param>
    private static void StripAdditionalProperties(JToken token)
    {
        if (token is JObject obj)
        {
            obj.Remove("additionalProperties");
            foreach (JProperty property in obj.Properties())
                StripAdditionalProperties(property.Value);
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
                StripAdditionalProperties(item);
        }
    }
}
