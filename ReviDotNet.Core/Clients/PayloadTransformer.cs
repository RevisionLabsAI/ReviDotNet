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

    private readonly InferClientConfig _config;

    public PayloadTransformer(InferClientConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    
        /// <summary>
    /// Transforms the standard payload format to Gemini's expected format.
    /// Adheres to Gemini’s REST schema for :generateContent/:streamGenerateContent.
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
        // Gemini does not support minP in generationConfig; ignore any provided min_p.
        
        if (payload.TryGetValue("max_tokens", out var maxTokens))
            generationConfig["maxOutputTokens"] = maxTokens;
        
        if (payload.TryGetValue("stop", out var stopSequences))
            generationConfig["stopSequences"] = stopSequences;

        // Supported penalties in Gemini (camelCase)
        if (payload.TryGetValue("frequency_penalty", out var freqPenalty))
            generationConfig["frequencyPenalty"] = freqPenalty;
        if (payload.TryGetValue("presence_penalty", out var presPenalty))
            generationConfig["presencePenalty"] = presPenalty;

        // Handle Gemini JSON Schema (responseJsonSchema)
        if (payload.TryGetValue("guided_json", out var jsonSchema))
        {
            try
            {
                var schemaToken = Newtonsoft.Json.Linq.JToken.Parse(jsonSchema.ToString());
                var schemaObject = ConvertJTokenToPlain(schemaToken) as Dictionary<string, object>;
                if (schemaObject != null)
                {
                    // responseJsonSchema (Gemini 2.5+/3.x) accepts standard JSON Schema, replacing
                    // the legacy OpenAPI-subset responseSchema that needed heavy sanitization.
                    // Only $schema is stripped — Gemini has no use for the meta-schema URI.
                    schemaObject.Remove("$schema");
                    generationConfig["responseJsonSchema"] = schemaObject;
                    generationConfig["responseMimeType"] = "application/json";
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                Util.Log($"Warning: Invalid JSON schema provided for Gemini: {jsonSchema}");
            }
        }
        
        // Native thinking. A numeric value is a token budget (Gemini 2.5 thinkingConfig.thinkingBudget);
        // a word is a level (Gemini 3 thinkingConfig.thinkingLevel). The model's thinking-conversion table
        // is expected to yield a numeric budget for 2.5-era models.
        if (payload.TryGetValue("thinking_mode", out var geminiThinking))
        {
            string mode = geminiThinking?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(mode))
            {
                var thinkingConfig = new Dictionary<string, object>();
                if (int.TryParse(mode, out int budget))
                    thinkingConfig["thinkingBudget"] = budget;
                else
                    thinkingConfig["thinkingLevel"] = mode.ToLowerInvariant();
                generationConfig["thinkingConfig"] = thinkingConfig;
            }
        }

        if (generationConfig.Any())
            geminiPayload["generationConfig"] = generationConfig;

        // Handle single prompt (Gemini has no top-level "prompt" field; use contents.parts[].text)
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
                .Select(m =>
                {
                    var parts = new List<object>();
                    if (!string.IsNullOrEmpty(m.Content))
                        parts.Add(new { text = m.Content });
                    if (m.Images != null)
                        foreach (var img in m.Images)
                            parts.Add(new { inlineData = new { mimeType = img.MediaType, data = img.Base64 } });
                    return new
                    {
                        role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : m.Role.ToLower(),
                        parts
                    };
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
                // Use Gemini REST schema (camelCase): { "tools": [ { "googleSearchRetrieval": {} } ] }
                geminiPayload["tools"] = new[] { new { googleSearchRetrieval = new { } } };
            }
        }
        
        // Note:
        // - Do NOT include model in the body (model is in the URL path).
        // - Do NOT include stream in the body; streaming is selected by calling :streamGenerateContent with alt=sse.
        // - repetition_penalty is not supported by Gemini and is therefore ignored if present in the input payload.

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
        
        // Anthropic requires max_tokens; provide a default if missing. 4096 (not the old 1024): this
        // fallback is a last resort for callers that configured nothing, and 1024 silently truncated
        // real answers and structured (JSON) outputs mid-document.
        if (payload.TryGetValue("max_tokens", out var maxTokens))
            outPayload["max_tokens"] = maxTokens;
        else
            outPayload["max_tokens"] = 4096;

        // Native thinking / reasoning. The mode is either an effort level (e.g. "high") which uses the
        // adaptive thinking API of newer models (Opus 4.8+):
        //     { "thinking": { "type": "adaptive" }, "output_config": { "effort": "high" } }
        // or a numeric token budget which uses the classic extended-thinking API (Claude 4.5-era):
        //     { "thinking": { "type": "enabled", "budget_tokens": N } }
        // Either way thinking requires temperature = 1 and disallows top_p; reasoning tokens count
        // toward the output budget, so for the budget API max_tokens is raised above the budget.
        if (payload.TryGetValue("thinking_mode", out var thinkObj))
        {
            string thinkingMode = thinkObj?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(thinkingMode))
            {
                if (int.TryParse(thinkingMode, out int thinkingBudget) && thinkingBudget > 0)
                {
                    outPayload["thinking"] = new Dictionary<string, object>
                    {
                        { "type", "enabled" },
                        { "budget_tokens", thinkingBudget }
                    };
                    int currentMax = outPayload.TryGetValue("max_tokens", out var mtObj)
                        && int.TryParse(mtObj?.ToString(), out int mt) ? mt : 0;
                    if (currentMax <= thinkingBudget)
                        outPayload["max_tokens"] = thinkingBudget + 4096; // leave room for the answer
                }
                else
                {
                    outPayload["thinking"] = new Dictionary<string, object> { { "type", "adaptive" } };
                    outPayload["output_config"] = new Dictionary<string, object>
                    {
                        { "effort", thinkingMode.ToLowerInvariant() }
                    };
                }

                outPayload["temperature"] = 1;   // required with thinking enabled
                outPayload.Remove("top_p");      // not permitted with thinking enabled
            }
        }

        // Structured outputs: output_config.format with a standard JSON schema (strict-style —
        // additionalProperties:false is injected the same way as for OpenAI). Merged into the
        // output_config the thinking branch may already have created for the effort level.
        if (payload.TryGetValue("guided_json", out var claudeSchema))
        {
            try
            {
                object processedSchema = Util.AddAdditionalPropertiesToSchema(claudeSchema.ToString() ?? string.Empty);
                Dictionary<string, object> outputConfig =
                    outPayload.TryGetValue("output_config", out var existing) && existing is Dictionary<string, object> dict
                        ? dict
                        : new Dictionary<string, object>();
                outputConfig["format"] = new Dictionary<string, object>
                {
                    { "type", "json_schema" },
                    { "schema", processedSchema }
                };
                outPayload["output_config"] = outputConfig;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                Util.Log($"Warning: Invalid JSON schema provided for Claude: {claudeSchema}");
            }
        }

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
                var contentBlocks = new List<object>();
                if (!string.IsNullOrEmpty(m.Content))
                    contentBlocks.Add(new Dictionary<string, object> { { "type", "text" }, { "text", m.Content } });
                if (m.Images != null)
                    foreach (var img in m.Images)
                        contentBlocks.Add(new Dictionary<string, object>
                        {
                            { "type", "image" },
                            { "source", new Dictionary<string, object> { { "type", "base64" }, { "media_type", img.MediaType }, { "data", img.Base64 } } }
                        });
                conv.Add(new Dictionary<string, object>
                {
                    { "role", role },
                    { "content", contentBlocks }
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
        bool? useSearchGrounding,
        string? thinking = null)
    {
        if (temperature.HasValue) parameters.Add("temperature", temperature.Value);
        if (topK.HasValue) parameters.Add("top_k", topK.Value);
        if (topP.HasValue) parameters.Add("top_p", topP.Value);
        if (minP.HasValue) parameters.Add("min_p", minP.Value);
        if (frequencyPenalty.HasValue) parameters.Add("frequency_penalty", frequencyPenalty.Value);
        if (presencePenalty.HasValue) parameters.Add("presence_penalty", presencePenalty.Value);
        if (repetitionPenalty.HasValue) parameters.Add("repetition_penalty", repetitionPenalty.Value);
        if (stopSequences is { Length: > 0 }) parameters.Add("stop", stopSequences);

        // Native thinking / reasoning. The resolved value is already provider-specific (the model's
        // thinking-conversion table translated the common word). Emit it in the right shape per provider:
        //  - Claude / Gemini: stash under "thinking_mode"; TransformTo{Claude,Gemini}Payload formats it.
        //  - OpenAI: reasoning models take a top-level "reasoning_effort" (low|medium|high|minimal).
        // Gated to thinking-capable protocols so others never see an unknown key in the verbatim payload.
        if (!string.IsNullOrWhiteSpace(thinking))
        {
            switch (_config.Protocol)
            {
                case Protocol.Claude:
                case Protocol.Gemini:
                    parameters["thinking_mode"] = thinking;
                    break;
                case Protocol.OpenAI:
                    parameters["reasoning_effort"] = thinking;
                    break;
            }
        }

        GuidanceType? chosenType = guidanceType ?? _config.DefaultGuidanceType;
        string? chosenString = guidanceString ?? _config.DefaultGuidanceString;
        //Util.Log($"guidanceString: {guidanceString}, _protocol: {_protocol}");
        
        // A null maxTokenType means the model rcfg set no max-token-type — the overwhelmingly common case.
        // It must still emit the configured max-tokens under the standard "max_tokens" key: previously a
        // null type matched neither case and the value was silently DROPPED, so providers that require the
        // field fell back to their transformer default (Claude: 1024), truncating long outputs. Models that
        // need the non-standard key (e.g. OpenAI reasoning models) declare max-token-type explicitly.
        switch (maxTokenType ?? MaxTokenType.MaxTokens)
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
            case Protocol.Perplexity:
            {
                // OpenAI Parameters - no bestOf support
                
                // OpenAI JSON Schema Guidance
                if (!_config.SupportsGuidance || string.IsNullOrEmpty(chosenString) || chosenType != GuidanceType.Json)
                {
                    return;
                }

                AddOpenAiJsonGuidance(parameters, chosenString);
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

                // vLLM removed the legacy guided_json/guided_regex/guided_decoding_backend request
                // fields in v0.12.0. Current forms (vLLM ≥ ~v0.10): the OpenAI-compatible
                // response_format json_schema for JSON, and the native structured_outputs extra-body
                // dict for regex. The decoding backend is server-side config now, not per-request.
                switch (chosenType)
                {
                    case GuidanceType.Json:
                        AddOpenAiJsonGuidance(parameters, chosenString);
                        break;
                    case GuidanceType.Regex:
                        parameters.Add("structured_outputs", new Dictionary<string, object>
                        {
                            { "regex", chosenString }
                        });
                        break;
                }

                break;
            }

            case Protocol.Claude:
            {
                // Claude structured outputs: the schema is stashed under "guided_json" here and
                // emitted as output_config.format by TransformToClaudePayload (which owns the
                // Anthropic payload shape, including merging with the effort/thinking output_config).
                if (!_config.SupportsGuidance || string.IsNullOrEmpty(chosenString) || chosenType != GuidanceType.Json)
                {
                    return;
                }

                parameters.Add("guided_json", chosenString);
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

    /// <summary>
    /// Emits OpenAI-style JSON guidance into the payload: strict
    /// <c>response_format: json_schema</c> by default, or the <c>json_object</c> downgrade (with the
    /// schema delivered as prompt text) when the provider is configured with
    /// <see cref="JsonSchemaMode.JsonObject"/>. Shared by the OpenAI/Perplexity and vLLM branches —
    /// modern vLLM accepts the same OpenAI-compatible shape.
    /// </summary>
    /// <param name="parameters">The payload dictionary to add the response format to.</param>
    /// <param name="schema">The JSON schema string.</param>
    private void AddOpenAiJsonGuidance(Dictionary<string, object> parameters, string schema)
    {
        // Some OpenAI-compatible hosts (e.g. Z.ai's GLM API) only accept
        // response_format {type:"json_object"} and reject the strict json_schema form.
        // json-schema-mode = json-object downgrades to that: valid JSON is still enforced
        // on the wire, and the schema travels as an extra system message so the model
        // knows the expected shape (conformance is then on the model, not the decoder).
        if (_config.JsonSchemaMode == JsonSchemaMode.JsonObject)
        {
            parameters.Add("response_format", new { type = "json_object" });
            AppendSchemaInstruction(parameters, schema);
            return;
        }

        try
        {
            // Parse the JSON schema string to ensure it's valid JSON
            object processedSchema = Util.AddAdditionalPropertiesToSchema(schema);

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
            Util.Log($"Warning: Invalid JSON schema provided for OpenAI: {schema}. Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites a Chat-Completions-style <c>response_format</c> into the OpenAI Responses API
    /// shape. The Responses API does not accept <c>response_format</c> at all — structured output
    /// lives under <c>text.format</c> with the schema fields flattened
    /// (<c>{type, name, schema, strict}</c> directly under <c>format</c>, no nested
    /// <c>json_schema</c> object). No-op when the payload carries no response_format.
    /// </summary>
    /// <param name="parameters">The /v1/responses payload dictionary to rewrite in place.</param>
    public static void ConvertResponseFormatForResponsesApi(Dictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("response_format", out object? responseFormat))
            return;

        parameters.Remove("response_format");

        // Round-trip through JSON so the anonymous-object shapes above become inspectable.
        Newtonsoft.Json.Linq.JObject rf = Newtonsoft.Json.Linq.JObject.FromObject(responseFormat);
        string? type = rf["type"]?.ToString();

        Dictionary<string, object> format;
        if (type == "json_schema" && rf["json_schema"] is Newtonsoft.Json.Linq.JObject js)
        {
            format = new Dictionary<string, object>
            {
                { "type", "json_schema" },
                { "name", js["name"]?.ToString() ?? "response_schema" },
                { "strict", js["strict"]?.ToObject<bool>() ?? true },
                { "schema", ConvertJTokenToPlain(js["schema"] ?? new Newtonsoft.Json.Linq.JObject())! }
            };
        }
        else
        {
            format = new Dictionary<string, object> { { "type", type ?? "json_object" } };
        }

        parameters["text"] = new Dictionary<string, object> { { "format", format } };
    }

    /// <summary>
    /// Delivers the JSON schema as prompt text for the <see cref="JsonSchemaMode.JsonObject"/>
    /// downgrade path, where <c>response_format: json_object</c> enforces valid JSON but carries no
    /// schema. Appends a system message to a copy of the message list (the caller's conversation
    /// history must not accumulate schema instructions), or appends to the raw prompt string for
    /// prompt-completion payloads.
    /// </summary>
    /// <param name="parameters">The payload dictionary containing "messages" or "prompt".</param>
    /// <param name="schema">The JSON schema string describing the expected output shape.</param>
    private static void AppendSchemaInstruction(Dictionary<string, object> parameters, string schema)
    {
        string instruction =
            "Respond with a single JSON object that conforms to the following JSON Schema. " +
            "Output only the JSON — no Markdown fences, no commentary.\n" + schema;

        if (parameters.TryGetValue("messages", out object? messagesObj) && messagesObj is List<Message> messages)
        {
            List<Message> augmented = [.. messages, new Message("system", instruction)];
            parameters["messages"] = augmented;
        }
        else if (parameters.TryGetValue("prompt", out object? promptObj) && promptObj is string prompt)
        {
            parameters["prompt"] = prompt + "\n\n" + instruction;
        }
    }
}