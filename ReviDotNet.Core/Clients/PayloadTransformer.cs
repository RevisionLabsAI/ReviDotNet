// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;
using System.Linq;
using System.Collections.Generic;

namespace Revi;

public class PayloadTransformer
{
    private static object? ConvertJTokenToPlain(Newtonsoft.Json.Linq.JToken token)
    {
        switch (token.Type)
        {
            case Newtonsoft.Json.Linq.JTokenType.Object:
                var obj = (Newtonsoft.Json.Linq.JObject)token;
                var dict = new Dictionary<string, object>();
                foreach (var prop in obj.Properties())
                {
                    var val = ConvertJTokenToPlain(prop.Value);
                    if (val != null)
                        dict[prop.Name] = val;
                }
                return dict;
            case Newtonsoft.Json.Linq.JTokenType.Array:
                var arr = (Newtonsoft.Json.Linq.JArray)token;
                var list = new List<object>();
                foreach (var item in arr)
                {
                    var v = ConvertJTokenToPlain(item);
                    list.Add(v!);
                }
                return list;
            case Newtonsoft.Json.Linq.JTokenType.Integer:
                return token.ToObject<long>();
            case Newtonsoft.Json.Linq.JTokenType.Float:
                return token.ToObject<double>();
            case Newtonsoft.Json.Linq.JTokenType.Boolean:
                return token.ToObject<bool>();
            case Newtonsoft.Json.Linq.JTokenType.String:
                return token.ToObject<string>();
            case Newtonsoft.Json.Linq.JTokenType.Null:
                return null;
            default:
                return token.ToString();
        }
    }

    // Gemini requires enums to be only on string types. This sanitizer ensures that.
    private static void SanitizeSchemaForGemini(Dictionary<string, object> schema)
    {
        void FixNode(Dictionary<string, object> node)
        {
            string type = node.TryGetValue("type", out var t) ? t?.ToString() ?? string.Empty : string.Empty;

            // Handle enums on non-string types by converting to string enum
            if (node.TryGetValue("enum", out var enumObj) && enumObj is IList<object> enumList)
            {
                if (!string.Equals(type, "string", StringComparison.OrdinalIgnoreCase))
                {
                    node["type"] = "string";
                    node["enum"] = enumList.Select(v => v?.ToString() ?? string.Empty).ToList();
                }
            }

            // Arrays: move enum from array node to items, and ensure items is string with enum when needed
            if (string.Equals(type, "array", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> itemsNode;
                if (node.TryGetValue("items", out var itemsObj) && itemsObj is Dictionary<string, object> dict)
                {
                    itemsNode = dict;
                }
                else
                {
                    itemsNode = new Dictionary<string, object>();
                    node["items"] = itemsNode;
                }

                // Recurse into items first
                FixNode(itemsNode);

                if (node.TryGetValue("enum", out var arrEnumObj) && arrEnumObj is IList<object> arrEnum)
                {
                    itemsNode["type"] = "string";
                    itemsNode["enum"] = arrEnum.Select(v => v?.ToString() ?? string.Empty).ToList();
                    node.Remove("enum");
                }
            }

            // Objects: recurse into properties
            if (string.Equals(type, "object", StringComparison.OrdinalIgnoreCase))
            {
                if (node.TryGetValue("properties", out var propsObj) && propsObj is Dictionary<string, object> props)
                {
                    foreach (var key in props.Keys.ToList())
                    {
                        if (props[key] is Dictionary<string, object> child)
                        {
                            FixNode(child);
                        }
                    }
                }
            }

            // Combinators: oneOf, anyOf, allOf
            foreach (var keyword in new[] { "oneOf", "anyOf", "allOf" })
            {
                if (node.TryGetValue(keyword, out var combo) && combo is IList<object> list)
                {
                    foreach (var item in list)
                    {
                        if (item is Dictionary<string, object> child)
                        {
                            FixNode(child);
                        }
                    }
                }
            }

            // Nested items (arrays of arrays)
            if (node.TryGetValue("items", out var nested) && nested is Dictionary<string, object> nestedDict)
            {
                FixNode(nestedDict);
            }
        }

        FixNode(schema);
    }
    private readonly InferClientConfig _config;

    public PayloadTransformer(InferClientConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    
        /// <summary>
    /// Transforms the standard payload format to Gemini's expected format.
    /// </summary>
    /// <param name="payload">The standard payload dictionary.</param>
    /// <returns>The transformed payload for Gemini API.</returns>
    public Dictionary<string, object> TransformToGeminiPayload(Dictionary<string, object> payload)
    {
        var geminiPayload = new Dictionary<string, object>();
        var generationConfig = new Dictionary<string, object>();
        
        // Transform generation config parameters
        if (payload.TryGetValue("temperature", out var temperature))
            generationConfig["temperature"] = temperature;
        
        if (payload.TryGetValue("top_k", out var topK))
            generationConfig["topK"] = topK;
        
        if (payload.TryGetValue("top_p", out var topP))
            generationConfig["topP"] = topP;
        
        if (payload.TryGetValue("min_p", out var minP))
            generationConfig["minP"] = minP;
        
        if (payload.TryGetValue("max_tokens", out var maxTokens))
            generationConfig["maxOutputTokens"] = maxTokens;
        
        if (payload.TryGetValue("stop", out var stopSequences))
            generationConfig["stopSequences"] = stopSequences;

        // Handle Gemini JSON Schema (responseSchema)
        if (payload.TryGetValue("guided_json", out var jsonSchema))
        {
            try
            {
                var schemaToken = Newtonsoft.Json.Linq.JToken.Parse(jsonSchema.ToString());
                var schemaObject = ConvertJTokenToPlain(schemaToken) as Dictionary<string, object>;
                if (schemaObject != null)
                {
                    // Sanitize schema for Gemini: enums must be on string types only
                    SanitizeSchemaForGemini(schemaObject);
                    generationConfig["responseSchema"] = schemaObject;
                    generationConfig["responseMimeType"] = "application/json";
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                Util.Log($"Warning: Invalid JSON schema provided for Gemini: {jsonSchema}");
            }
        }
        
        if (generationConfig.Any())
            geminiPayload["generationConfig"] = generationConfig;
        
        // Handle single prompt
        if (payload.TryGetValue("prompt", out var promptValue) && promptValue is string prompt)
        {
            geminiPayload["contents"] = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            };
        }
        // Handle chat messages
        else if (payload.TryGetValue("messages", out var messagesValue) && messagesValue is List<Message> messages)
        {
            Message? systemMessage = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
            if (systemMessage != null)
            {
                geminiPayload["systemInstruction"] = new { parts = new[] { new { text = systemMessage.Content } } };
            }

            var contents = messages
                .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(m => new
                {
                    role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : m.Role.ToLower(),
                    parts = new[] { new { text = m.Content } }
                })
                .ToList();
            
            geminiPayload["contents"] = contents;
        }
        
        // Add Google Search grounding tool if requested per request (prompt or model override)
        if (payload.TryGetValue("use_search_grounding", out var useGroundingObj))
        {
            bool enable = false;
            if (useGroundingObj is bool b)
                enable = b;
            else if (bool.TryParse(useGroundingObj?.ToString(), out var parsed))
                enable = parsed;

            if (enable)
            {
                // Use snake_case key to match Gemini REST API: { "tools": [ { "google_search": {} } ] }
                geminiPayload["tools"] = new[] { new { google_search = new { } } };
            }
        }
        
        // Note: Gemini doesn't support frequency_penalty, presence_penalty, or best_of
        // These parameters are ignored for Gemini requests

        return geminiPayload;
    }

    public Dictionary<string, object> TransformToClaudePayload(Dictionary<string, object> payload)
    {
        // Anthropic Messages API payload
        var outPayload = new Dictionary<string, object>();
        
        // Required fields
        if (payload.TryGetValue("model", out var model))
            outPayload["model"] = model;
        
        // Map parameters supported by Anthropic
        if (payload.TryGetValue("temperature", out var temperature))
            outPayload["temperature"] = temperature;
        if (payload.TryGetValue("top_p", out var topP))
            outPayload["top_p"] = topP;
        // map stop sequences
        if (payload.TryGetValue("stop", out var stop))
            outPayload["stop_sequences"] = stop;
        
        // Anthropic requires max_tokens; provide default if missing
        if (payload.TryGetValue("max_tokens", out var maxTokens))
            outPayload["max_tokens"] = maxTokens;
        else
            outPayload["max_tokens"] = 1024;
        
        // Stream flag
        if (payload.TryGetValue("stream", out var streamObj))
            outPayload["stream"] = streamObj;
        
        // Handle prompt or messages
        if (payload.TryGetValue("prompt", out var promptObj) && promptObj is string prompt)
        {
            // Single-turn: use user message
            outPayload["messages"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", new List<object> { new Dictionary<string, object>{{"type","text"},{"text", prompt}} } }
                }
            };
        }
        else if (payload.TryGetValue("messages", out var messagesObj) && messagesObj is List<Message> messages)
        {
            // Extract optional system message
            string? systemText = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content;
            if (!string.IsNullOrWhiteSpace(systemText))
                outPayload["system"] = systemText;
            
            var conv = new List<object>();
            foreach (var m in messages)
            {
                if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                    continue;
                string role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                conv.Add(new Dictionary<string, object>
                {
                    { "role", role },
                    { "content", new List<object> { new Dictionary<string, object>{{"type","text"},{"text", m.Content}} } }
                });
            }
            outPayload["messages"] = conv;
        }
        
        return outPayload;
    }

    /// <summary>
    /// Adds optional parameters to the specified dictionary.
    /// </summary>
    /// <param name="parameters">The dictionary to add the optional parameters to.</param>
    /// <param name="temperature">The temperature parameter value.</param>
    /// <param name="topP">The top_p parameter value.</param>
    /// <param name="topK">The top_k parameter value.</param>
    /// <param name="bestOf">The best_of parameter value.</param>
    /// <param name="maxTokens">The max_tokens parameter value.</param>
    /// <param name="frequencyPenalty">The frequencyPenalty parameter value.</param>
    /// <param name="presencePenalty">The presencePenalty parameter value.</param>
    /// <param name="stopSequences">The stopSequences parameter value.</param>
    /// <param name="guidanceType">The guidanceType parameter value.</param>
    /// <param name="guidanceString">The guidanceString parameter value.</param>
    public void AddOptionalParameters(
        Dictionary<string, object> parameters,
        float? temperature,
        int? topK,
        float? topP,
        float? minP,
        int? bestOf,
        MaxTokenType? maxTokenType,
        int? maxTokens,
        float? frequencyPenalty,
        float? presencePenalty,
        float? repetitionPenalty,
        string[]? stopSequences,
        GuidanceType? guidanceType,
        string? guidanceString,
        bool? useSearchGrounding)
    {
        if (temperature.HasValue) parameters.Add("temperature", temperature.Value);
        if (topK.HasValue) parameters.Add("top_k", topK.Value);
        if (topP.HasValue) parameters.Add("top_p", topP.Value);
        if (minP.HasValue) parameters.Add("min_p", minP.Value);
        if (frequencyPenalty.HasValue) parameters.Add("frequency_penalty", frequencyPenalty.Value);
        if (presencePenalty.HasValue) parameters.Add("presence_penalty", presencePenalty.Value);
        if (repetitionPenalty.HasValue) parameters.Add("repetition_penalty", repetitionPenalty.Value);
        if (stopSequences is { Length: > 0 }) parameters.Add("stop", stopSequences);

        GuidanceType? chosenType = guidanceType ?? _config.DefaultGuidanceType;
        string? chosenString = guidanceString ?? _config.DefaultGuidanceString;
        //Util.Log($"guidanceString: {guidanceString}, _protocol: {_protocol}");
        
        switch (maxTokenType)
        {
            case MaxTokenType.MaxTokens:
                if (maxTokens.HasValue) parameters.Add("max_tokens", maxTokens.Value);
                break;
            
            case MaxTokenType.MaxCompletionTokens:
                if (maxTokens.HasValue) parameters.Add("max_completion_tokens", maxTokens.Value);
                break;
        }
        
        switch (_config.Protocol)
        {
            case Protocol.OpenAI:
            {
                // OpenAI Parameters - no bestOf support
                
                // OpenAI JSON Schema Guidance
                if (!_config.SupportsGuidance || string.IsNullOrEmpty(chosenString) || chosenType != GuidanceType.Json)
                {
                    return;
                }

                try
                {
                    // Parse the JSON schema string to ensure it's valid JSON
                    var processedSchema = Util.AddAdditionalPropertiesToSchema(chosenString);

                    
                    // OpenAI uses response_format with json_schema type
                    parameters.Add("response_format", new
                    {
                        type = "json_schema",
                        json_schema = new
                        {
                            name = "response_schema",
                            strict = true,
                            schema = processedSchema
                        }
                    });
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    Util.Log($"Warning: Invalid JSON schema provided for OpenAI: {chosenString}. Error: {ex.Message}");
                }
                
                break;
            }

            case Protocol.vLLM:
            {
                // vLLM Parameters
                if (bestOf.HasValue) parameters.Add("best_of", bestOf.Value);

                // vLLM Guidance
                if (!_config.SupportsGuidance || string.IsNullOrEmpty(chosenString))
                {
                    //Util.Log($"{_supportsGuidance} : {chosenString}");
                    return;
                }

                switch (chosenType)
                {
                    case GuidanceType.Json:
                        parameters.Add("guided_json", chosenString);
                        //parameters.Add("guided_decoding_backend", "outlines");
                        parameters.Add("guided_decoding_backend", "outlines");
                        break;
                    case GuidanceType.Regex:
                        parameters.Add("guided_regex", chosenString);
                        parameters.Add("guided_decoding_backend", "lm-format-enforcer");
                        //parameters.Add("guided_decoding_backend", "outlines");
                        break;
                    /*case GuidanceType.Choice:
                        guidance.Add("guided_choice", _defaultGuidance);
                        guidance.Add("guided_decoding_backend", "lm-format-enforcer");
                        break;*/
                }

                break;
            }

            case Protocol.LLamaAPI:
            {
                // LLamaAPI Parameters
                
                // LLamaAPI Guidance
                if (!_config.SupportsGuidance || string.IsNullOrEmpty(chosenString)) 
                    return;
                
                switch (chosenType)
                {
                    case GuidanceType.Json:
                        parameters.Add("json_schema", chosenString);
                        break;
                    case GuidanceType.Grammar:
                        parameters.Add("grammar", chosenString);
                        break;
                }

                break;
            }

            case Protocol.Gemini:
            {
                // Gemini doesn't support bestOf, frequencyPenalty, presencePenalty
                // Note: Gemini doesn't support regex guidance or other guidance types
                // Parameters will be transformed in TransformToGeminiPayload method

                if (useSearchGrounding is not null)
                {
                    parameters.Add("use_search_grounding", useSearchGrounding);
                }
                
                // Gemini JSON Schema Guidance
                if (!_config.SupportsGuidance || string.IsNullOrEmpty(chosenString) || chosenType != GuidanceType.Json)
                {
                    //Util.Log($"{_supportsGuidance} : {chosenString}");
                    return;
                }
                
                // Add guided_json parameter which will be transformed to responseSchema in TransformToGeminiPayload
                parameters.Add("guided_json", chosenString);
                break;
            }
        }
    }
}