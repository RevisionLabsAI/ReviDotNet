// =================================================================================
//   Copyright © 2024 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
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
	
	public static string ExtractJson(string? input, bool? chainOfThought = false)
	{
		if (string.IsNullOrEmpty(input))
			return "";
		
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
					//reasoningText = parts[0].Trim();
					textToJsonify = parts[1].Trim();
				}
			}
		}

		if (string.IsNullOrEmpty(textToJsonify)) 
			textToJsonify = input;
		
		// We have some text which we want to be Json
		try
		{
			// Check whether it's valid json with newtonsoft
			var parsedJson = JsonDocument.Parse(textToJsonify);
			
			// If we make it here, we're good already. Nothing to do. 
			return input;
		}
		catch
		{
			// Ignored
		}

		return "";
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