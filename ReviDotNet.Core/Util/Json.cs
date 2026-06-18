// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Json.Schema.Generation;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Json.Schema;

namespace Revi;

// This file is bleh, please ignore how bleh this file is. Thank you. :) 

public static partial class Util
{
	public static string ExtractTextAfterOutput(string input)
	{
		// Define the marker we are looking for
		string marker = "Output:";

		// Find the index of the marker in the input string
		int index = input.IndexOf(marker);

		// Check if the marker exists in the string
		if (index != -1)
		{
			// The +1 is to start after the marker; adjust if you need to include or exclude certain characters
			// The index + marker.Length gives the start position right after the marker
			return input.Substring(index + marker.Length).Trim(); // Trim to remove any leading or trailing whitespace
		}

		// Return an empty string or null if the marker is not found
		// You could also throw an exception or handle this case differently depending on your application needs
		return "";
	}

	public static string JsonStringFromType(Type type)
	{
		var config = new SchemaGeneratorConfiguration
		{
			PropertyNameResolver = PropertyNameResolvers.KebabCase,
			Nullability = Nullability.Disabled
		};
		var schema = new JsonSchemaBuilder().FromType(type, config).Build();
		JsonSerializerOptions options = new ()
		{
			WriteIndented = true
		};
		//Util.Log($"JsonStringFromType: \n'''\n{JsonSerializer.Serialize(schema, options)}\n'''\n\n");
		return JsonSerializer.Serialize(schema);
	}
	
	/// <summary>
	/// Extracts a valid JSON document from arbitrary model output. Handles the common ways chat-tuned
	/// models wrap JSON: Markdown code fences (```json ... ```), surrounding prose, and (when
	/// <paramref name="chainOfThought"/> is true) reasoning that precedes an "Output:"-style marker.
	/// Returns the extracted JSON substring (validated by parsing), or "" when no valid JSON can be recovered.
	/// </summary>
	/// <param name="input">The raw model output.</param>
	/// <param name="chainOfThought">When true, isolates the text after a known output marker before extraction.</param>
	public static string ExtractJson(string? input, bool? chainOfThought = false)
	{
		if (string.IsNullOrEmpty(input))
			return "";

		string text = input;

		// 1. Optionally isolate the content after a chain-of-thought output marker.
		if (chainOfThought is true)
		{
			string[] possibleMarkers = { "output:", "result:", "answer:", "response:", "conclusion:", "solution:", "### output" };
			string? marker = possibleMarkers.FirstOrDefault(m => text.ToLower().Contains(m));
			if (marker != null)
			{
				string[] parts = text.Split(new string[] { marker }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length > 1)
					text = parts[1].Trim();
			}
		}

		// 2. Strip Markdown code fences (```json ... ``` or ``` ... ```).
		text = StripCodeFences(text);

		// 3. Fast path: the (possibly de-fenced) text is already valid JSON.
		if (TryParseJson(text, out string valid))
			return valid;

		// 4. Bound to the outermost { } or [ ] region (drops surrounding prose) and retry.
		string bounded = ExtractBracketRegion(text);
		if (!string.IsNullOrEmpty(bounded) && TryParseJson(bounded, out string boundedValid))
			return boundedValid;

		// 5. Last resort: lightweight repairs (trailing commas, unbalanced braces/brackets).
		string repairTarget = string.IsNullOrEmpty(bounded) ? text : bounded;
		string repaired = TryLightweightJsonFixes(repairTarget);
		if (!string.IsNullOrEmpty(repaired) && TryParseJson(repaired, out string repairedValid))
			return repairedValid;

		return "";
	}

	/// <summary>Returns the contents of the first Markdown code fence if present, otherwise the trimmed input.</summary>
	private static string StripCodeFences(string text)
	{
		if (string.IsNullOrEmpty(text) || !text.Contains("```"))
			return text.Trim();

		Match m = Regex.Match(text, "```(?:json|JSON)?\\s*([\\s\\S]*?)```");
		if (m.Success)
			return m.Groups[1].Value.Trim();

		// Unterminated fence — strip the opening marker(s) and continue with the remainder.
		return text.Replace("```json", string.Empty)
				   .Replace("```JSON", string.Empty)
				   .Replace("```", string.Empty)
				   .Trim();
	}

	/// <summary>True if <paramref name="candidate"/> parses as JSON; the trimmed text is returned via <paramref name="normalized"/>.</summary>
	private static bool TryParseJson(string candidate, out string normalized)
	{
		normalized = "";
		if (string.IsNullOrWhiteSpace(candidate))
			return false;
		try
		{
			using JsonDocument _ = JsonDocument.Parse(candidate);
			normalized = candidate.Trim();
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>Returns the substring spanning the outermost {...} or [...] region, or "" if none is found.</summary>
	private static string ExtractBracketRegion(string input)
	{
		int firstCurly = input.IndexOf('{');
		int firstSquare = input.IndexOf('[');
		int lastCurly = input.LastIndexOf('}');
		int lastSquare = input.LastIndexOf(']');

		bool hasObject = firstCurly >= 0 && lastCurly > firstCurly;
		bool hasArray = firstSquare >= 0 && lastSquare > firstSquare;

		if (hasObject && hasArray)
		{
			// Use whichever bracket type encloses the other.
			return (firstCurly < firstSquare && lastCurly > lastSquare)
				? input.Substring(firstCurly, lastCurly - firstCurly + 1)
				: input.Substring(firstSquare, lastSquare - firstSquare + 1);
		}
		if (hasObject)
			return input.Substring(firstCurly, lastCurly - firstCurly + 1);
		if (hasArray)
			return input.Substring(firstSquare, lastSquare - firstSquare + 1);

		return "";
	}

	/// <summary>Applies cheap, deterministic JSON repairs: removes trailing commas and balances braces/brackets.</summary>
	private static string TryLightweightJsonFixes(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return "";

		// Remove trailing commas immediately before a closing } or ].
		string fixedJson = Regex.Replace(json, @",(?=\s*[}\]])", string.Empty);

		int openCurly = fixedJson.Count(c => c == '{');
		int closeCurly = fixedJson.Count(c => c == '}');
		int openSquare = fixedJson.Count(c => c == '[');
		int closeSquare = fixedJson.Count(c => c == ']');

		if (openCurly > closeCurly)
			fixedJson += new string('}', openCurly - closeCurly);
		if (openSquare > closeSquare)
			fixedJson += new string(']', openSquare - closeSquare);

		return fixedJson;
	}

	public static string JsonifyExample(string input, bool? requestJson = false, bool? chainOfThought = false)
	{
		if (string.IsNullOrEmpty(input))
		{
			//Util.Log("Input is empty");
			return "";
		}

		if (requestJson != true)
		{
			//Util.Log("Not requesting json");
			return input;
		}

		string reasoningText = "";
		string textToJsonify = "";

		// Define possible markers for splitting text to isolate potential JSON content
		string[] possibleMarkers = { "output:", "result:", "answer:", "response:", "conclusion:", "solution:", "### output" };

		if (chainOfThought is true)
		{
			// Convert the input to lower case for case-insensitive comparison and check for markers
			string? marker = possibleMarkers.FirstOrDefault(m => input.ToLower().Contains(m));

			if (marker != null)
			{
				// Split input text using the found marker, selecting the text after it
				string[] parts = input.Split(new string[] { marker }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length > 1)
				{
					reasoningText = parts[0].Trim();
					textToJsonify = parts[1].Trim();
				}
			}
		}

		if (string.IsNullOrEmpty(textToJsonify)) 
			textToJsonify = input;
		
		// We have some text which we want to be Json
		try
		{
			//Util.Log("We made it here");
			// First check whether it's already valid json with newtonsoft
			var jsonDocument = JsonDocument.Parse(textToJsonify);
			// If we make it here, we're good already. Nothing to do. 
			return input;
		}
		catch
		{
			//Util.Log("Not valid json...");
			// That failed, lets try YAML parsing
			try
			{
				string jsonOutput = ConvertYamlToJson(textToJsonify);
				if (!string.IsNullOrEmpty(jsonOutput))
					return reasoningText + jsonOutput;
			}
			catch
			{
				Log("WARNING: JsonifyExample: Could not get valid Json or Yaml when it was expected");
			}
		}

		return input;
	}
	
	public static string ConvertYamlToJson(string yaml)
	{
		var deserializer = new DeserializerBuilder()
			.WithNamingConvention(UnderscoredNamingConvention.Instance) // or choose another convention
			.Build();

		// Deserialize the YAML string into an object
		var yamlObject = deserializer.Deserialize<object>(yaml);

		// Serialize the object into JSON
		JsonSerializerOptions options = new() { WriteIndented = true };
		return JsonSerializer.Serialize(yamlObject, options);
	}
	
	public static string ConvertJsonToYaml(string json)
	{
		// Deserialize the JSON string into an object
		var jsonObject = JsonSerializer.Deserialize<object>(json);

		var serializer = new SerializerBuilder()
			.WithNamingConvention(UnderscoredNamingConvention.Instance) 
			.Build();

		// Serialize the object into YAML
		string yaml = serializer.Serialize(jsonObject);

		return yaml;
	}
	
	public static T? GetObjectFromJson<T>(string json)
	{
		// Create the options for the JSON serializer
		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,

            Converters ={
				new JsonStringEnumConverter()
			}
		};

		// Load the object from the JSON file
		try
		{ 
			var result = JsonSerializer.Deserialize<T>(json, options);
			return result;
		}
		catch (Exception e)
		{
			Log($"Util.GetObjectFromJSON(): Failed to deserialized JSON into object: {e.Message}");
			return default;
		}
	}

	public static string PrettifyJson(string jsonString)
    {
        using (JsonDocument jsonDocument = JsonDocument.Parse(jsonString))
		{
            // Convert the JsonDocument back to a pretty-printed JSON string
            string prettifiedJson = 
            System.Text.Json.JsonSerializer.Serialize(
				jsonDocument.RootElement,
				new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            return prettifiedJson;
        }
    }
	
	public static string? RemoveEnclosingQuotes(string? input)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		// Remove whitespace from beginning and end
		input = input.Trim();

		// Remove beginning quotation marks
		if (input.Length >= 2 && input[0] == '"')
		{
			return input.Substring(1, input.Length - 1);
		}
		
		// Remove trailing quotation marks
		if (input.Length >= 2 && input[^1] == '"')
		{
			return input.Substring(0, input.Length - 1);
		}

		return input;
	}
	
	
public static object AddAdditionalPropertiesToSchema(string schemaString)
{
    try
    {
        // Parse the schema using JsonDocument to avoid JsonElement issues
        using var document = JsonDocument.Parse(schemaString);
        var cleanSchema = ConvertToCleanDictionary(document.RootElement);
        
        // Ensure root is an object type
        if (!cleanSchema.ContainsKey("type") || cleanSchema["type"]?.ToString() != "object")
        {
            cleanSchema["type"] = "object";
        }
        
        // Process recursively to add additionalProperties and ensure required fields
        ProcessSchemaForOpenAI(cleanSchema);
        
        return cleanSchema;
    }
    catch (Exception ex)
    {
        Util.Log($"Error processing JSON schema: {ex.Message}");
        
        // Return a basic valid schema as fallback
        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>(),
            ["required"] = new List<string>(),
            ["additionalProperties"] = false
        };
    }
}

private static Dictionary<string, object> ConvertToCleanDictionary(JsonElement element)
{
    var result = new Dictionary<string, object>();
    
    foreach (var property in element.EnumerateObject())
    {
        result[property.Name] = ConvertJsonElementToCleanValue(property.Value);
    }
    
    return result;
}

private static object ConvertJsonElementToCleanValue(JsonElement element)
{
    return element.ValueKind switch
    {
        JsonValueKind.Object => ConvertToCleanDictionary(element),
        JsonValueKind.Array => element.EnumerateArray()
            .Select(ConvertJsonElementToCleanValue)
            .ToList(),
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.TryGetInt32(out var intVal) ? intVal : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.ToString()
    };
}

private static void ProcessSchemaForOpenAI(Dictionary<string, object> schema)
{
    // Add additionalProperties: false for object types
    if (schema.TryGetValue("type", out var typeValue) && typeValue?.ToString() == "object")
    {
        if (!schema.ContainsKey("additionalProperties"))
        {
            schema["additionalProperties"] = false;
        }
        
        // For OpenAI: ensure required array includes ALL properties
        if (schema.TryGetValue("properties", out var propertiesObj) && 
            propertiesObj is Dictionary<string, object> properties)
        {
            // Get all property keys
            var allPropertyKeys = properties.Keys.ToList();
            
            // Set required to include all properties (OpenAI requirement)
            schema["required"] = allPropertyKeys;
            
            // Process nested properties
            foreach (var prop in properties.Values.OfType<Dictionary<string, object>>())
            {
                ProcessSchemaForOpenAI(prop);
            }
        }
        else
        {
            // If no properties, ensure required is still an empty array
            schema["required"] = new List<string>();
        }
    }
    
    // Process items for arrays
    if (schema.TryGetValue("items", out var itemsObj) && 
        itemsObj is Dictionary<string, object> items)
    {
        ProcessSchemaForOpenAI(items);
    }
    
    // Process oneOf, anyOf, allOf
    foreach (var keyword in new[] { "oneOf", "anyOf", "allOf" })
    {
        if (schema.TryGetValue(keyword, out var arrayObj) && 
            arrayObj is List<object> schemaArray)
        {
            foreach (var item in schemaArray.OfType<Dictionary<string, object>>())
            {
                ProcessSchemaForOpenAI(item);
            }
        }
    }
}


	
	/*public static string ExtractJSON(string input)
	{
		int firstOpenCurly = input.IndexOf('{');
		int firstOpenSquare = input.IndexOf('[');

		int lastCloseCurly = input.LastIndexOf('}');
		int lastCloseSquare = input.LastIndexOf(']');

		// This checks for both {} and [].
		if (firstOpenCurly >= 0 && lastCloseCurly >= firstOpenCurly
		                                      && firstOpenSquare >= 0 && lastCloseSquare >= firstOpenSquare)
		{
			// If {} are the outermost, use them, otherwise use [].
			if (firstOpenCurly < firstOpenSquare
			    && lastCloseCurly > lastCloseSquare)
			{
				return input.Substring(firstOpenCurly, lastCloseCurly - firstOpenCurly + 1);
			}
			else
			{
				return input.Substring(firstOpenSquare, lastCloseSquare - firstOpenSquare + 1);
			}
		}

		// This checks for {} only.
		if (firstOpenCurly >= 0 && lastCloseCurly >= firstOpenCurly)
		{
			return input.Substring(firstOpenCurly, lastCloseCurly - firstOpenCurly + 1);
		}
    
		// This checks for [] only.
		if (firstOpenSquare >= 0 && lastCloseSquare >= firstOpenSquare)
		{
			return input.Substring(firstOpenSquare, lastCloseSquare - firstOpenSquare + 1);
		}

		return "";
	}*/
	
	/// <summary>
	/// Extracts and attempts to auto-correct JSON from a given string, optionally searching for JSON data after specified output markers.
	/// </summary>
	/// <param name="input">The input string from which to extract and auto-correct JSON.</param>
	/// <param name="searchAfterOutputMarker">Specifies whether to search for JSON after an output marker.</param>
	/// <returns>The extracted and auto-corrected JSON string, or an empty string if no valid JSON data is found.</returns>
	/*public static string ExtractAndCorrectJSON(string input, bool searchAfterOutputMarker = false)
	{
		string rawJson = ExtractJSON(input, searchAfterOutputMarker);
		if (string.IsNullOrEmpty(rawJson))
			return "";

		try
		{
			// Attempt to parse the JSON to see if it's valid
			var parsedJson = JToken.Parse(rawJson);
			return parsedJson.ToString();
		}
		catch (JsonReaderException)
		{
			// If JSON is malformed, attempt to auto-correct common issues
			return TryFix(rawJson);
		}
	}*/
	
	/// <summary>
    /// Extracts JSON from a given string, optionally searching for JSON data after specified output markers.
    /// </summary>
    /// <param name="input">The input string from which to extract JSON.</param>
    /// <param name="chainOfThought">Specifies whether to search for JSON after an output marker.</param>
    /// <returns>The extracted JSON string, or an empty string if no JSON data is found following the specified criteria.</returns>
    /*public static string ExtractJSON(string input, bool chainOfThought = false)
    {
        string searchText = input;

        // Define possible markers for splitting text to isolate potential JSON content
        string[] possibleMarkers = { "output:", "result:", "answer:", "response:", "conclusion:", "solution:" };

        if (chainOfThought)
        {
            // Convert the input to lower case for case-insensitive comparison and check for markers
            var marker = possibleMarkers.FirstOrDefault(m => input.ToLower().Contains(m));

            if (marker != null)
            {
                // Split input text using the found marker, selecting the text after it
                string[] parts = input.Split(new string[] { marker }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    searchText = parts[1].TrimStart(); // Use the text part after the marker
                }
                else
                {
                    return ""; // Return an empty string if no content is found after the marker
                }
            }
            else
            {
                return ""; // No marker found; return an empty string
            }
        }

        // Try to find the bounds of the JSON structure
        int firstOpenCurly = searchText.IndexOf('{');
        int firstOpenSquare = searchText.IndexOf('[');
        int lastCloseCurly = searchText.LastIndexOf('}');
        int lastCloseSquare = searchText.LastIndexOf(']');

        // Determine the outermost JSON structure
        if (firstOpenCurly >= 0 && lastCloseCurly >= firstOpenCurly
            && firstOpenSquare >= 0 && lastCloseSquare >= firstOpenSquare)
        {
            if (firstOpenCurly < firstOpenSquare && lastCloseCurly > lastCloseSquare)
            {
                return searchText.Substring(firstOpenCurly, lastCloseCurly - firstOpenCurly + 1);
            }
            else if (firstOpenSquare < firstOpenCurly && lastCloseSquare > lastCloseCurly)
            {
                return searchText.Substring(firstOpenSquare, lastCloseSquare - firstOpenSquare + 1);
            }
        }

        // Check for a single type of JSON structure
        if (firstOpenCurly >= 0 && lastCloseCurly >= firstOpenCurly)
        {
            return searchText.Substring(firstOpenCurly, lastCloseCurly - firstOpenCurly + 1);
        }
        if (firstOpenSquare >= 0 && lastCloseSquare >= firstOpenSquare)
        {
            return searchText.Substring(firstOpenSquare, lastCloseSquare - firstOpenSquare + 1);
        }

        return ""; // No JSON structure found; return an empty string
    }*/
	
	/// <summary>
	/// Attempts to correct common JSON formatting errors.
	/// </summary>
	/// <param name="json">The JSON string to correct.</param>
	/// <returns>The corrected JSON string, or an empty string if the JSON cannot be corrected.</returns>
	/*private static string TryFix(string json)
	{
		// Remove illegal characters like trailing commas at the end of lists and objects
		json = Regex.Replace(json, @"\,(?=\s*?[\}\]])", string.Empty);

		// Ensure all keys and string values are correctly quoted
		json = Regex.Replace(json, @"([\{\s,])(\w+)(\s*:\s*)", "$1\"$2\"$3");
		json = Regex.Replace(json, @":\s*([\w]+)\s*([,\}])", ": \"$1\"$2");

		// Try to balance unbalanced curly braces and square brackets
		int curlyOpens = json.Count(c => c == '{');
		int curlyCloses = json.Count(c => c == '}');
		int squareOpens = json.Count(c => c == '[');
		int squareCloses = json.Count(c => c == ']');

		// Add missing closing braces or brackets at the end if necessary
		if (curlyOpens > curlyCloses) {
			json += new string('}', curlyOpens - curlyCloses);
		}
		if (squareOpens > squareCloses) {
			json += new string(']', squareOpens - squareCloses);
		}

		try
		{
			// Re-validate the potentially corrected JSON
			var parsedJson = JToken.Parse(json);
			return parsedJson.ToString(Formatting.Indented); // Return formatted JSON
		}
		catch (JsonReaderException)
		{
			// If it still fails, return empty string indicating correction failure
			return "";
		}
	}*/
}