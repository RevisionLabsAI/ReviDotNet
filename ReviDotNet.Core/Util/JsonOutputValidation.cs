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

    /// <summary>Per-(prompt, type) memo of the example check, so it runs once per process.</summary>
    private static readonly ConcurrentDictionary<string, byte> _examplesChecked = new();

    /// <summary>
    /// Unwraps a model output that arrived as a single-key object wrapping the real answer —
    /// <c>{"TypeName": {...}}</c> — when the outer shape does not conform to the type but the
    /// inner one does. Models copy that shape from few-shot examples authored with a type-name
    /// root; a provider with constrained decoding suppresses it, a provider that validates after
    /// generation rejects it outright (Groq, 400 <c>json_validate_failed</c>), and a provider that
    /// enforces nothing hands it back as-is, where it deserializes into an object with every field
    /// null. This is the client-side half of the fix: the prompt-side half is
    /// <see cref="WarnIfExamplesDoNotConform"/>, which points at the example that taught the shape.
    /// </summary>
    /// <param name="json">The extracted JSON output.</param>
    /// <param name="outputType">The C# type the output will be deserialized into.</param>
    /// <param name="promptName">The prompt name, for log messages.</param>
    /// <param name="unwrapped">The inner object when unwrapping applied; otherwise the input.</param>
    /// <returns><see langword="true"/> when the output was unwrapped.</returns>
    public static bool TryUnwrapSingleRoot(string? json, Type? outputType, string promptName, out string unwrapped)
    {
        unwrapped = json ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json) || outputType is null)
            return false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (node is not JsonObject outer || outer.Count != 1 || outer.First().Value is not JsonObject inner)
            return false;

        // Only when the wrapper is the problem: an outer object that already conforms is the
        // answer (a type with one object-valued property looks exactly like a wrapper).
        if (ConformsToType(json, outputType, promptName))
            return false;

        string innerJson = inner.ToJsonString();
        if (!ConformsToType(innerJson, outputType, promptName))
            return false;

        Util.Log(
            $"JsonOutputValidation: output for prompt '{promptName}' arrived wrapped under '{outer.First().Key}' " +
            $"and was unwrapped to the {outputType.Name} the schema describes. Check the prompt's examples: " +
            "a type-name root in an [[_exout_N]] block teaches this shape.");
        unwrapped = innerJson;
        return true;
    }

    /// <summary>
    /// Warns, once per prompt and output type, when a few-shot example does not conform to the
    /// schema the provider is asked to enforce. An example is the strongest instruction a prompt
    /// gives; one that contradicts the schema teaches the model a shape that a strict provider
    /// rejects and a lax one returns broken. Thirteen prompts did exactly that for months
    /// unnoticed, because constrained-decoding providers silently overrode them.
    /// </summary>
    /// <param name="prompt">The prompt whose examples to check.</param>
    /// <param name="outputType">The type the caller will deserialize into.</param>
    public static void WarnIfExamplesDoNotConform(Prompt prompt, Type? outputType)
    {
        if (outputType is null || prompt.Examples is null || prompt.Examples.Count == 0)
            return;

        string key = $"{prompt.Name}|{outputType.FullName}";
        if (!_examplesChecked.TryAdd(key, 0))
            return;

        for (int index = 0; index < prompt.Examples.Count; index++)
        {
            string output = prompt.Examples[index].Output;
            if (string.IsNullOrWhiteSpace(output))
                continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(output);
            }
            catch (System.Text.Json.JsonException)
            {
                continue; // Not JSON at all: the request-json loader already warned about that.
            }

            if (ConformsToType(output, outputType, prompt.Name ?? "?"))
                continue;

            string hint = node is JsonObject wrapper && wrapper.Count == 1 && wrapper.First().Value is JsonObject
                ? $" It is wrapped under '{wrapper.First().Key}': drop that root line from the [[_exout_{index + 1}]] block and dedent the rest."
                : string.Empty;
            Util.Log(
                $"WARNING: prompt '{prompt.Name}' example #{index + 1} does not conform to the schema of {outputType.Name} " +
                $"that the provider is asked to enforce; the model is being taught a shape the provider may reject.{hint}");
        }
    }

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
