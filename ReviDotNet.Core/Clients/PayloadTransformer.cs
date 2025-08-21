using Newtonsoft.Json;

namespace Revi;

public class PayloadTransformer
{
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
                // Parse the JSON schema string to ensure it's valid JSON
                var schemaObject = JsonConvert.DeserializeObject(jsonSchema.ToString());
                generationConfig["responseSchema"] = schemaObject;
                generationConfig["responseMimeType"] = "application/json";
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // If parsing fails, treat it as a string (though this shouldn't happen with valid JSON schema)
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
        
        // Note: Gemini doesn't support frequency_penalty, presence_penalty, or best_of
        // These parameters are ignored for Gemini requests

        return geminiPayload;
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
        string? guidanceString)
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