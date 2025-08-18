// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
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

using System.Collections;
using System.Data;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Revi;

public class Infer
{
	// ===================
	//  Inference Calling 
	// ===================
	
	#region Completion Inference
	/// <summary>
	/// Calls the inference process for completing a prompt.
	/// </summary>
	/// <param name="promptObject">The prompt object to use for inference.</param>
	/// <param name="prompt">The prompt information.</param>
	/// <param name="model">The model profile to use for inference.</param>
	/// <param name="outputObject">The optional output object.</param>
	/// <returns>The completion response from the inference process.</returns>
	private static async Task<CompletionResponse?> CallInference(
		object promptObject,
		Prompt prompt,
		ModelProfile model,
		Type? outputType = null,
		CancellationToken token = default)
	{
		// Debug
		Util.Log($"CallInference(prompt: '{prompt.Name}', model: '{model.Name}');");
		
		CompletionResponse? response = null;
		try
		{
			// Declarations
			int totalLength;
			
			// Sanity checks
		
			// Prompt needs to be compatible
			// Provider needs to be enabled and have an inferenceclient that is active
			if (model.Provider.InferenceClient is null)
				throw new Exception("InferenceClient is null!");
		
			// Model needs to be enabled and compatible
		
			// Process parameters
		
			// Figure out our guidance strategy here
			GetGuidance(
				prompt, 
				model,
				outputType, 
				out GuidanceType? guidanceType, 
				out string? guidanceString);

			switch (promptObject)
			{
				case string promptString:
				{
					//Util.Log("Calling Prompt Inference");

					// Prompt specific checks
					totalLength = promptString.Length;
					if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
						throw new Exception("Too many tokens!");

					// Prompt completion
					response = await model.Provider.InferenceClient.GenerateAsync(
						prompt: promptString,
						model: model.ModelString,
						temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
						topK: (int?)SelectParam(model.TopK, prompt.TopK),
						topP: (float?)SelectParam(model.TopP, prompt.TopP),
						minP: (float?)SelectParam(model.MinP, prompt.MinP),
						bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
						maxTokenType: model.MaxTokenType,
						maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
						frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
						presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
						repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
						stopSequences: ToArray(model.StopSequences),
						guidanceType: guidanceType,
						guidanceString: guidanceString,
						cancellationToken: token);
					break;
				}

				case List<Message> messages:
				{
					//Util.Log("Calling Chat Inference");

					// Chat specific checks
					totalLength = messages.Sum(msg => msg.Role.Length + msg.Content.Length);
					if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
						throw new Exception("Too many tokens!");

					// Chat completion
					response = await model.Provider.InferenceClient.GenerateAsync(
						messages: messages,
						model: model.ModelString,
						temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
						topK: (int?)SelectParam(model.TopK, prompt.TopK),
						topP: (float?)SelectParam(model.TopP, prompt.TopP),
						minP: (float?)SelectParam(model.MinP, prompt.MinP),
						bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
						maxTokenType: model.MaxTokenType,
						maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
						frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
						presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
						repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
						stopSequences: ToArray(model.StopSequences),
						guidanceType: guidanceType,
						guidanceString: guidanceString,
						cancellationToken: token);
					break;
				}
			}
		}
		catch (Exception e)
		{
			Util.Log($"CallInference Exception: \"{e.Message}\"");
		}
		
		// Logging and Observability
		// Update to match new response object
		var providerString = ""; //= JsonConvert.SerializeObject(providerInfo, Formatting.Indented);
		var dump =
			$"CallInference:\n\nProviderInfo:\n{providerString}\n\nMessages:\n{prompt}\n\nOutput:\n";
		await Util.DumpLog(dump, "inference");

		return response;
	}

	private static Prompt FindPrompt(string name)
	{
		Prompt? foundPrompt = PromptManager.Get(name);
		if (foundPrompt is null)
			throw new Exception($"Could not find specified prompt: {name}");
		
		return foundPrompt;
	}
	
	public static ProviderProfile FindProvider(Prompt prompt, string provider)
	{
		ProviderProfile? foundProvider = ProviderManager.Get(provider);
		if (foundProvider is null)
			throw new Exception($"Could not find specified provider: {provider}");

		return foundProvider;

	}

	/// <summary>
	/// Generates completion response based on the provided prompt, inputs, model profile, model name, and output object.
	/// </summary>
	/// <param name="prompt">The prompt to generate completion.</param>
	/// <param name="inputs">The list of inputs to be considered along with prompt.</param>
	/// <param name="modelProfile">The model profile to be used for completion. Can be null.</param>
	/// <param name="modelName">The name of the model. Can be null.</param>
	/// <param name="outputType">The type of the output object. Can be null.</param>
	/// <returns>The completion response generated by the completion method.</returns>
	public static async Task<CompletionResponse?> Completion(
		Prompt prompt,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		Type? outputType = null,
		CancellationToken token = default)
	{
		// Declarations 
		CompletionResponse? result;
		string promptString;
		List<Message> messages;

		// Find the model
		ModelProfile foundModel = FindModel(prompt, modelProfile, modelName);

		// Validate that the prompt is put together well
		//  - If chain-of-thought, check that it has the keywords "reasoning" or "explain" if settings_enforcecot
		//  - If json, check that it has the keywords "json"
		
		// Check the inputs
		// System, instruction, formatting, and examples are from a safe source.  Inputs however could be subject to 
		// prompt injection.  Inputs must be checked as that is where external content comes in to the prompt. 
		if (await FilterCheck(prompt, inputs, token))
			throw new SecurityException("FilterCheck failed!");
		
		// Find the completion method
		if (!Enum.TryParse(prompt.CompletionType, out CompletionType type))
		{
			throw new Exception($"Invalid completion type: '{prompt.CompletionType}'");
		}
		
		// Now lets build the prompt, call inference, and collect the result
		switch (type)
		{
			case CompletionType.ChatOnly:
				messages = CompletionChat.BuildMessages(prompt, foundModel, inputs);
				result = await CallInference(messages, prompt, foundModel, outputType, token);
				break;
    
			case CompletionType.PromptOnly:
				promptString = CompletionPrompt.BuildString(prompt, foundModel, inputs);
				result = await CallInference(promptString, prompt, foundModel, outputType, token);
				break;

			case CompletionType.PromptChatOne:
			case CompletionType.PromptChatMulti:
			{
				if (foundModel.Provider.SupportsCompletion ?? false)
				{
					promptString = CompletionPrompt.BuildString(prompt, foundModel, inputs);
					result = await CallInference(promptString, prompt, foundModel, outputType, token);
				}
				else
				{
					messages = CompletionChat.BuildMessages(prompt, foundModel, inputs);
					result = await CallInference(messages, prompt, foundModel, outputType, token);
				}

				break;
			}

			default:
				throw new Exception($"Unexpected completion type: '{prompt.CompletionType}'");
		}
		
		// TODO: Any checks to do on the result?
		
		// Return the resulting output
		return result;
	}
	#endregion
	
	#region Streaming Inference
	/// <summary>
	/// Calls the streaming inference process for completing a prompt.
	/// </summary>
	/// <param name="promptObject">The prompt object to use for inference.</param>
	/// <param name="prompt">The prompt information.</param>
	/// <param name="model">The model profile to use for inference.</param>
	/// <param name="outputType">The optional output object type.</param>
	/// <param name="cancellationToken">Cancellation token for the operation.</param>
	/// <returns>An async enumerable that yields text chunks as they are generated.</returns>
	private static async IAsyncEnumerable<string> CallStreamingInference(
		object promptObject,
		Prompt prompt,
		ModelProfile model,
		Type? outputType = null,
		[EnumeratorCancellation] CancellationToken token = default)
	{
		// Debug
		Util.Log($"CallStreamingInference(prompt: '{prompt.Name}', model: '{model.Name}');");
		
		// Declarations
		int totalLength;
		StreamingResult<string> streamResult;
		
		// Sanity checks
		if (model.Provider.InferenceClient is null)
			throw new Exception("InferenceClient is null!");
	
		// Figure out our guidance strategy here
		GetGuidance(
			prompt, 
			model,
			outputType, 
			out GuidanceType? guidanceType, 
			out string? guidanceString);

		try
		{
			switch (promptObject)
			{
				case string promptString:
				{
					// Prompt specific checks
					totalLength = promptString.Length;
					if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
						throw new Exception("Too many tokens!");

					// Streaming prompt completion
					streamResult = model.Provider.InferenceClient.GenerateStreamAsync(
						prompt: promptString,
						model: model.ModelString,
						temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
						topK: (int?)SelectParam(model.TopK, prompt.TopK),
						topP: (float?)SelectParam(model.TopP, prompt.TopP),
						minP: (float?)SelectParam(model.MinP, prompt.MinP),
						bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
						maxTokenType: model.MaxTokenType,
						maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
						frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
						presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
						repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
						stopSequences: ToArray(model.StopSequences),
						guidanceType: guidanceType,
						guidanceString: guidanceString,
						cancellationToken: token);
					break;
				}

				case List<Message> messages:
				{
					// Chat specific checks
					totalLength = messages.Sum(msg => msg.Role.Length + msg.Content.Length);
					if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
						throw new Exception("Too many tokens!");

					// Streaming chat completion
					streamResult = model.Provider.InferenceClient.GenerateStreamAsync(
						messages: messages,
						model: model.ModelString,
						temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
						topK: (int?)SelectParam(model.TopK, prompt.TopK),
						topP: (float?)SelectParam(model.TopP, prompt.TopP),
						minP: (float?)SelectParam(model.MinP, prompt.MinP),
						bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
						maxTokenType: model.MaxTokenType,
						maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
						frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
						presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
						repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
						stopSequences: ToArray(model.StopSequences),
						guidanceType: guidanceType,
						guidanceString: guidanceString,
						cancellationToken: token);
					break;
				}

				default:
					throw new Exception($"Unexpected prompt object type: {promptObject.GetType()}");
			}
		}
		catch (Exception e)
		{
			Util.Log($"CallStreamingInference Exception: \"{e.Message}\"");
			throw;
		}
		
    bool hasYieldedAnyChunks = false;
    
    await foreach (var chunk in streamResult.Stream)
    {
        hasYieldedAnyChunks = true;
        yield return chunk;
    }
    
    var metadata = await streamResult.Completion;
    if (!metadata.IsSuccess)
    {
        string errorMsg = $"Streaming failed: {metadata.ErrorMessage}";
        Util.Log(errorMsg);
        
        // If we haven't yielded anything yet, this is likely a complete failure
        if (!hasYieldedAnyChunks)
        {
            throw new Exception($"Streaming inference failed: {metadata.ErrorMessage}");
        }
    }
}

	/// <summary>
	/// Generates streaming completion response based on the provided prompt, inputs, model profile, model name, and output object.
	/// </summary>
	/// <param name="prompt">The prompt to generate completion.</param>
	/// <param name="inputs">The list of inputs to be considered along with prompt.</param>
	/// <param name="modelProfile">The model profile to be used for completion. Can be null.</param>
	/// <param name="modelName">The name of the model. Can be null.</param>
	/// <param name="outputType">The type of the output object. Can be null.</param>
	/// <param name="cancellationToken">Cancellation token for the operation.</param>
	/// <returns>An async enumerable that yields streaming text chunks as they are generated.</returns>
	public static async IAsyncEnumerable<string> CompletionStream(
		Prompt prompt,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		Type? outputType = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		// Find the model
		ModelProfile foundModel = FindModel(prompt, modelProfile, modelName);

		// Check the inputs for potential prompt injection
		if (await FilterCheck(prompt, inputs))
			throw new SecurityException("FilterCheck failed!");
		
		// Find the completion method
		if (!Enum.TryParse(prompt.CompletionType, out CompletionType type))
		{
			throw new Exception($"Invalid completion type: '{prompt.CompletionType}'");
		}
		
		IAsyncEnumerable<string> streamResult;
		
		// Build the prompt and call streaming inference
		switch (type)
		{
			case CompletionType.ChatOnly:
				var messages = CompletionChat.BuildMessages(prompt, foundModel, inputs);
				streamResult = CallStreamingInference(messages, prompt, foundModel, outputType, cancellationToken);
				break;
    
			case CompletionType.PromptOnly:
				var promptString = CompletionPrompt.BuildString(prompt, foundModel, inputs);
				streamResult = CallStreamingInference(promptString, prompt, foundModel, outputType, cancellationToken);
				break;

			case CompletionType.PromptChatOne:
			case CompletionType.PromptChatMulti:
			{
				if (foundModel.Provider.SupportsCompletion ?? false)
				{
					var promptStr = CompletionPrompt.BuildString(prompt, foundModel, inputs);
					streamResult = CallStreamingInference(promptStr, prompt, foundModel, outputType, cancellationToken);
				}
				else
				{
					var msgs = CompletionChat.BuildMessages(prompt, foundModel, inputs);
					streamResult = CallStreamingInference(msgs, prompt, foundModel, outputType, cancellationToken);
				}
				break;
			}

			default:
				throw new Exception($"Unexpected completion type: '{prompt.CompletionType}'");
		}
		
		await foreach (var chunk in streamResult)
		{
			yield return chunk;
		}
	}
	#endregion

	
	// ===================
	//  Object Converters
	// ===================
	
	#region Object Converters
	public static async Task<T?> ToObject<T>(
		string promptName,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		int retryAttempt = 0,
		int? originalRetryLimit = null,
		CancellationToken token = default)
	{
		// Declarations
		Type outputType = typeof(T);
		T? newObject = default;
		string? extractedJson = null;
		
		// Find the prompt from the provided promptName
		Prompt prompt = FindPrompt(promptName);

		// Throw an exception if the prompt isn't requesting json output from the model
		// TODO: Support markdown conversion
		if (prompt.RequestJson is false)
			throw new Exception($"Infer.ToObject: RequestJson is false for prompt '{prompt.Name}'");
		
		// Prompt for completion!
		CompletionResponse? result = await Completion(
			prompt,
			inputs,
			modelProfile,
			modelName,
			outputType, 
			token);

		// Try to convert the completion output into the requested object
		try
		{
			//Util.Log($"CompletionResponse: \n'''\n{JsonConvert.SerializeObject(result, Formatting.Indented)}\n'''\n");
			extractedJson = Util.ExtractJson(result?.Selected, prompt.ChainOfThought);
			
			if (string.IsNullOrEmpty(extractedJson))
			{
				string serializedResult = JsonConvert.SerializeObject(result, Formatting.Indented);
				throw new NoNullAllowedException(
					$"InferToObject(): json was null or empty: \n'''\n{serializedResult}\n'''\n");
			}
			var settings = new JsonSerializerSettings
				{ Converters = new List<JsonConverter> { new StringEnumConverter() } };
			
			newObject = JsonConvert.DeserializeObject<T>(extractedJson, settings);
		}
		
		// Deserialization failed
		catch (Exception e)
		{
			// Check whether we had any text to work with
			if (string.IsNullOrEmpty(extractedJson))
			{
				Util.Log($"WARNING: Missing JSON from Infer.ToObject output: \n{e.Message}\n");
				return default;
			}

			// Debugging
			Util.Log($"WARNING: Caught faulty JSON output:\n'''{extractedJson}\n'''");
			var dump = $"Faulty JSON!\nPrompt: {prompt.Name}\nFull Prompt: {result?.FullPrompt}\nOutput: {result?.Selected}";
			await Util.DumpLog(dump, "faultyjson");
			
			// Run the json-fixer prompt to see if we can make this work
			result = await Completion(
				FindPrompt("json-fixer"),
				new List<Input>()
				{
					new Input("Schema", Util.JsonStringFromType(outputType)), 
					new Input("Bad JSON", extractedJson)
				},
				null, 
				null,
				outputType,
				token);

			// Extract json from the new output
			extractedJson = Util.ExtractJson(result?.Selected, prompt.ChainOfThought);

			// 
			try
			{
				if (string.IsNullOrEmpty(extractedJson) is false)
				{
					var settings = new JsonSerializerSettings
					{
						Converters = new List<JsonConverter> { new StringEnumConverter() }
					};
					newObject = JsonConvert.DeserializeObject<T>(extractedJson, settings);
				}
			}
			catch
			{
				Util.Log($"JSON remediation FAILED with output:\n {extractedJson}\n\n");
			}
			finally
			{
				Util.Log($"JSON remediation {((newObject is not null) ? "SUCCEEDED!" : "FAILED")}");
			}
		}
		
		// Validate object and return it if it's valid
		var castObject = (T?) Convert.ChangeType(newObject, typeof(T));
		if (ValidateObject<T>(castObject, prompt))
			return castObject;
		
		Util.Log($"Infer.ToObject() object was invalid for prompt '{prompt.Name}'");
		
		// Otherwise, check if we're going to retry
		if (originalRetryLimit is null)
			originalRetryLimit = prompt.RetryAttempts;
		
		if (retryAttempt < (originalRetryLimit ?? 0))
		{
			Util.Log($"Retrying Infer.ToObject() for prompt '{prompt.Name}'");
			
			string promptToRetry = promptName;
			if (prompt.RetryPrompt is not null)
				promptToRetry = prompt.RetryPrompt;
			
			return await ToObject<T>(
				promptToRetry, 
				inputs, 
				modelProfile, 
				modelName, 
				retryAttempt + 1, 
				originalRetryLimit,
				token);
		}

		// TODO: Check if there's a fallback response to provide, otherwise provide what we generated
		return castObject;
	}
	
	private static bool ValidateObject<T>(object? inputObject, Prompt prompt)
	{
		if (prompt.RequireValidOutput is false)
			return true;
		
		if (inputObject is null)
			return false;
		
		return RecursivelyValidateObject(inputObject);
	}
	
	private static bool RecursivelyValidateObject(object obj)
	{
		try
		{
			foreach (var property in obj.GetType().GetProperties())
			{
				object? value = null;
				
				try
				{
					// Safely get the property value
					if (property.CanRead && property.GetIndexParameters().Length == 0)
					{
						value = property.GetValue(obj);
					}
				}
				catch (Exception ex)
				{
					Util.Log($"Warning: Could not read property '{property.Name}': {ex.Message}");
					continue;
				}
				
				// Handle DBNull values - convert to null for validation purposes
				if (value is DBNull)
				{
					value = null;
				}
				
				// Check Required attribute
				try
				{
					if (property.GetCustomAttributes(false).Any(attr => attr.GetType().Name == "RequiredAttribute"))
					{
						if (value == null || (value is string str && string.IsNullOrEmpty(str)))
						{
							Util.Log($"Validation failed: Required property '{property.Name}' is null or empty");
							return false;
						}
					}
				}
				catch (Exception ex)
				{
					Util.Log($"Warning: Could not check Required attribute for property '{property.Name}': {ex.Message}");
				}
				
				// Check collection constraints - simplified approach
				if (value is ICollection collection)
				{
					try
					{
						var attributes = property.GetCustomAttributes(false);
						
						foreach (var attr in attributes)
						{
							var attrType = attr.GetType();
							
							// Check for MinItems-like attributes
							if (attrType.Name.Contains("MinItems") || attrType.Name.Contains("MinLength"))
							{
								try
								{
									// Try different common property names for the minimum value
									var minValue = GetAttributeIntValue(attr, new[] { "Value", "MinItems", "MinLength", "Minimum" });
									if (minValue.HasValue && collection.Count < minValue.Value)
									{
										Util.Log($"Validation failed: '{property.Name}' has {collection.Count} items, minimum: {minValue.Value}");
										return false;
									}
								}
								catch (Exception ex)
								{
									Util.Log($"Warning: Could not validate MinItems for property '{property.Name}': {ex.Message}");
								}
							}
							
							// Check for MaxItems-like attributes
							if (attrType.Name.Contains("MaxItems") || attrType.Name.Contains("MaxLength"))
							{
								try
								{
									// Try different common property names for the maximum value
									var maxValue = GetAttributeIntValue(attr, new[] { "Value", "MaxItems", "MaxLength", "Maximum" });
									if (maxValue.HasValue && collection.Count > maxValue.Value)
									{
										Util.Log($"Validation failed: '{property.Name}' has {collection.Count} items, maximum: {maxValue.Value}");
										return false;
									}
								}
								catch (Exception ex)
								{
									Util.Log($"Warning: Could not validate MaxItems for property '{property.Name}': {ex.Message}");
								}
							}
						}
					}
					catch (Exception ex)
					{
						Util.Log($"Warning: Could not validate collection constraints for property '{property.Name}': {ex.Message}");
					}
				}
				
				// Recursively validate complex objects
				if (value != null && !Util.IsSimpleType(property.PropertyType))
				{
					try
					{
						if (value is IEnumerable enumerable && !(value is string))
						{
							foreach (var item in enumerable)
							{
								if (item != null && !RecursivelyValidateObject(item))
									return false;
							}
						}
						else if (!RecursivelyValidateObject(value))
						{
							return false;
						}
					}
					catch (Exception ex)
					{
						Util.Log($"Warning: Could not recursively validate property '{property.Name}': {ex.Message}");
					}
				}
			}
			
			// Also check fields (like NodeSummaryFromPartials.Label)
			foreach (var field in obj.GetType().GetFields())
			{
				object? value = null;
				
				try
				{
					value = field.GetValue(obj);
				}
				catch (Exception ex)
				{
					Util.Log($"Warning: Could not read field '{field.Name}': {ex.Message}");
					continue;
				}
				
				// Handle DBNull values - convert to null for validation purposes
				if (value is DBNull)
				{
					value = null;
				}
				
				try
				{
					if (field.GetCustomAttributes(false).Any(attr => attr.GetType().Name == "RequiredAttribute"))
					{
						if (value == null || (value is string str && string.IsNullOrEmpty(str)))
						{
							Util.Log($"Validation failed: Required field '{field.Name}' is null or empty");
							return false;
						}
					}
				}
				catch (Exception ex)
				{
					Util.Log($"Warning: Could not check Required attribute for field '{field.Name}': {ex.Message}");
				}
			}
		}
		catch (Exception ex)
		{
			Util.Log($"Warning: General validation error for object of type '{obj.GetType().Name}': {ex.Message}");
			// Continue with validation rather than failing completely
		}
		
		return true;
	}

	// Helper method to safely extract integer values from attributes
	private static int? GetAttributeIntValue(object attribute, string[] possiblePropertyNames)
	{
		foreach (var propertyName in possiblePropertyNames)
		{
			try
			{
				var property = attribute.GetType().GetProperty(propertyName);
				if (property != null)
				{
					var value = property.GetValue(attribute);
					if (value != null && value != DBNull.Value)
					{
						return Convert.ToInt32(value);
					}
				}
			}
			catch
			{
				// Continue to next property name
			}
		}
		return null;
	}

	
	public static async Task<JObject?> ToJObject(
		string promptName,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		JObject? newObject = null;
		try
		{
			string? output = await ToString(promptName, inputs, modelProfile, modelName, token);
			
			if (output is not null)
				newObject = JObject.Parse(output);
		}
		catch (Exception e)
		{
			Console.WriteLine(e);
		}
		
		// Return the resulting output
		return newObject;
	}
	
	public static async Task<bool?> ToBool(
		string promptName,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		string? result = await ToString(promptName, inputs, modelProfile, modelName, token);
		switch (result?.ToLower())
		{
			case "true":
				return true;
				
			case "false":
				return false;
			
			default:
				return null;
		}
	}
	#endregion
	
	
	// =======================
	//  Convenience Overloads
	// =======================

	#region Convenience Overloads
	/// <summary>
	/// Performs completion of the given prompt using the specified inputs and model profile.
	/// </summary>
	/// <param name="promptName">The prompt to complete.</param>
	/// <param name="inputs">The list of inputs to provide for completion. Default is null.</param>
	/// <param name="modelProfile">The model profile to use for completion. Default is null.</param>
	/// <param name="modelName">The name of the model to use for completion. Default is null.</param>
	/// <param name="outputType">The output object to deserialize the completion response into. Default is null.</param>
	/// <returns>The completion response, or null if the completion request fails.</returns>
	public static async Task<CompletionResponse?> Completion(
		string promptName,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		Type? outputType = null,
		CancellationToken token = default)
	{
		return await Completion(
			FindPrompt(promptName),
			inputs,
			modelProfile,
			modelName,
			outputType, 
			token);
	}
	
	public static async Task<string?> ToString(
		string promptName,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		CompletionResponse? result = await Completion(
			promptName, 
			inputs, 
			modelProfile, 
			modelName, 
			token: token);
		
		return result?.Selected;
	}
	
	public static async Task<string?> ToString(
		string promptName,
		Input? input,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		List<Input>? inputs = (input is not null) ? (new List<Input>() { input }) : null;
		return await ToString(promptName, inputs, modelProfile, modelName, token);
	}
	
	public static async Task<List<string>> ToStringList(
		string promptName,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		int retryAttempt = 0,
		int? originalRetryLimit = null,
		CancellationToken token = default)
	{
		Prompt prompt = FindPrompt(promptName);
		try
		{
			CompletionResponse? completion = await Completion(
				prompt,
				inputs,
				modelProfile,
				modelName);
			
			if (completion is null)
				throw new Exception("Infer.ToStringList(): Null completion!");
			
			if (string.IsNullOrEmpty(completion.Selected))
				throw new Exception("Infer.ToStringList(): Null or empty selected string!");
			
			List<string> lines = completion.Selected.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.Trim())
				.ToList();
			
			return lines;
		}
		catch (Exception e)
		{
			// Otherwise, check if we're going to retry
			if (originalRetryLimit is null)
				originalRetryLimit = prompt.RetryAttempts;
		
			if (retryAttempt < (originalRetryLimit ?? 0))
			{
				Util.Log($"Retrying Infer.ToStringList() for prompt '{prompt.Name}'");
			
				string promptToRetry = promptName;
				if (prompt.RetryPrompt is not null)
					promptToRetry = prompt.RetryPrompt;
			
				return await ToStringList(
					promptToRetry, 
					inputs, 
					modelProfile, 
					modelName, 
					retryAttempt + 1, 
					originalRetryLimit, 
					token);
			}
			
			// If we're here, we completely failed
			throw;
		}
	}
	
	public static async Task<List<string>> ToStringList(
		string promptName,
		Input? input,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		List<Input>? inputs = (input is not null) ? (new List<Input>() { input }) : null;
		return await ToStringList(
			promptName, 
			inputs, 
			modelProfile, 
			modelName, 
			token: token);
	}

	/// <summary>
	/// Generates a streaming list of strings from completion with an evaluator function that can stop the stream early.
	/// </summary>
	/// <param name="promptName">The prompt to complete.</param>
	/// <param name="inputs">The list of inputs to provide for completion.</param>
	/// <param name="modelProfile">The model profile to use for completion.</param>
	/// <param name="modelName">The name of the model to use for completion.</param>
	/// <param name="maxLines">Optional maximum number of lines to generate before stopping the stream.</param>
	/// <param name="evaluator">Function called after each chunk that returns true to stop the stream.</param>
	/// <param name="token">Cancellation token for the operation.</param>
	/// <returns>A list of strings split by newlines, potentially limited by the evaluator or maxLines.</returns>
	public static async Task<List<string>> ToStringListLimited(
		string promptName,
		List<Input>? inputs = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		int? maxLines = null,
		Func<string, bool>? evaluator = null,
		CancellationToken token = default)
	{
		var lines = new List<string>();
		var currentLine = new StringBuilder();
		var allContent = new StringBuilder();
		int completedLineCount = 0;
		
		try
		{
			var prompt = FindPrompt(promptName);
			var streamResult = CompletionStream(
				prompt, 
				inputs, 
				modelProfile, 
				modelName, 
				null, 
				token);
			
			await foreach (var chunk in streamResult)
			{
				Util.Log($"Chunk received: {chunk}");
				allContent.Append(chunk);
				
				// Process each character in the chunk to detect newlines
				foreach (char c in chunk)
				{
					if (c == '\n')
					{
						// Complete the current line and add it to the list
						var completedLine = currentLine.ToString().Trim();
						if (!string.IsNullOrEmpty(completedLine))
						{
							lines.Add(completedLine);
							completedLineCount++;
						}
						currentLine.Clear();
						
						// Check built-in line limit first (most efficient)
						if (maxLines.HasValue && completedLineCount >= maxLines.Value)
						{
							return lines;
						}
						
						// Check custom evaluator if provided
						if (evaluator?.Invoke(allContent.ToString()) == true)
						{
							return lines;
						}
					}
					else if (c != '\r') // Skip carriage returns
					{
						currentLine.Append(c);
					}
				}
			}
			
			// Add any remaining content as the last line
			var finalLine = currentLine.ToString().Trim();
			if (!string.IsNullOrEmpty(finalLine))
			{
				lines.Add(finalLine);
			}
		}
		catch (OperationCanceledException)
		{
			// Expected when cancellation is requested
			throw;
		}
		catch (Exception e)
		{
			Util.Log($"ToStringListLimited exception: {e.Message}");
			throw;
		}
		
		return lines;
	}
	
	/// <summary>
	/// Overload for ToStringListLimited with a single input.
	/// </summary>
	public static async Task<List<string>> ToStringListLimited(
		string promptName,
		Input? input = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		int? maxLines = null,
		Func<string, bool>? evaluator = null,
		CancellationToken token = default)
	{
		List<Input>? inputs = (input is not null) ? new List<Input>() { input } : null;
		return await ToStringListLimited(promptName, inputs, modelProfile, modelName, maxLines, evaluator, token);
	}


	
	public static async Task<bool?> ToBool(
		string promptName,
		Input? input = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		List<Input>? inputs = (input is not null) ? (new List<Input>() { input }) : null;
		return await ToBool(promptName, inputs, modelProfile, modelName, token);
	}
	
	public static async Task<JObject?> ToJObject(
		string promptName,
		Input? input = null,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		List<Input>? inputs = (input is not null) ? (new List<Input>() { input }) : null;
		return await ToJObject(promptName, inputs, modelProfile, modelName, token);
	}
	
	public static async Task<T?> ToObject<T>(
		string promptName,
		Input? input,
		ModelProfile? modelProfile = null,
		string? modelName = null,
		CancellationToken token = default)
	{
		List<Input>? inputs = (input is not null) ? (new List<Input>() { input }) : null;
		return await ToObject<T>(promptName, inputs, modelProfile, modelName, token: token);
	}
	#endregion
	
	
	// ======================
	//  Supporting Functions
	// ======================
	
	#region Supporting Functions
	/*
	// TODO: Make schema validation work
	public static bool ValidateToSchema(string output, string? schema)
	{
		if (string.IsNullOrEmpty(schema))
			return true;

		// if schema exists, safely check that the output serializes into it
		// output is a json object, schema is a json schema
		// Check whether the json object matches the json schema
		
		return RlonValidation.CompareSchema(output, schema);
	}*/
	
	private static object? SelectParam(string? modelString, object? promptObj)
	{
		if (modelString is null)
			return promptObj;
		
		if (modelString is "disabled")
			return promptObj;

		switch (promptObj)
		{
			case string: return modelString;
			case int: return int.Parse(modelString);
			case float: return float.Parse(modelString);
			default: throw new Exception($"Unexpected type: {promptObj.GetType()}");
		}
	}
	
	private static string[]? ToArray(string? input)
	{
		if (string.IsNullOrWhiteSpace(input)) return null;
		return input.Split(' ');
	}
	
	private static ModelProfile FindModel(Prompt prompt, ModelProfile? modelProfile, string? modelName)
	{
		// Find a model profile for this prompt
		ModelProfile? foundModel = null;
		
		// Check if we were provided a ModelProfile object
		if (modelProfile is not null)
		{
			if (modelProfile.Enabled)
				return modelProfile;

			Util.Log("modelProfile");
			throw new Exception($"Specified modelProfile but the model '{modelProfile.Name}' was not enabled.");
		}

		// Check if we were provided a modelString
		if (!string.IsNullOrEmpty(modelName))
		{
			foundModel = ModelManager.Get(modelName);
			if ((foundModel is not null) && foundModel.Enabled)
				return foundModel;

			Util.Log("modelString");
			throw new Exception($"Specified modelString but an enabled model named '{modelName}' could not be found.");
		}
		
		// Nope, check to see if the prompt had a model preference we can use
		if (!string.IsNullOrEmpty(prompt.ModelPref))
		{
			foundModel = ModelManager.Get(prompt.ModelPref);
			if ((foundModel is not null) && foundModel.Enabled)
				return foundModel;
		}

		// Still nope, check to see if we can find any compatible models which are available
		// TODO: Allow global setting that forces chat completion for prompt completion prompts
		foundModel = ModelManager.Find(prompt.MinTier, prompt.IsCompletion());
		if ((foundModel is not null) && foundModel.Enabled)
			return foundModel;
		
		// TODO: Global setting which allows using sub-par models when other models are unavailable
		foundModel = ModelManager.Find(ModelTier.C, false);
		if ((foundModel is not null) && foundModel.Enabled)
		{
			//Util.Log(
			//	$"WARNING: Using sub-par model (prompt '{prompt.Name}' wants tier '{prompt.MinTier}' but we " + 
			//         $"had to settle for model '{foundModel.Name}' which is tier '{foundModel.Tier}'");
			return foundModel;
		}

		// Everything failed, we're boned
		throw new AggregateException($"Could not find model for prompt '{prompt.Name}'");
	}

	/// <summary>
	/// Generates a string representation of a list of inputs.
	/// </summary>
	/// <param name="model">The ModelProfile object that contains information about the input item template.</param>
	/// <param name="inputs">The list of Input objects to generate the string representation from.</param>
	/// <returns>A string representation of the inputs.</returns>
	public static string? ListInputs(ModelProfile model, List<Input>? inputs)
	{
		if (inputs is null || !inputs.Any())
		{
			return null;
		}
		
		var templateLine = model.InputItemMulti;
		if (inputs.Count == 1)
		{
			templateLine = model.InputItem;
		}
		
		var inputString = "";
		for (var num = 0; num < inputs.Count; ++num)
		{
			var line = templateLine;
			line = line.Replace("{iterator}", (num + 1).ToString());
			line = line.Replace("{label}", inputs[num].Label);
			line = line.Replace("{text}", inputs[num].Text);
			inputString += line;
		}

		return inputString;
	}

	private static async Task<bool> FilterCheck(Prompt prompt, List<Input>? inputs, CancellationToken token = default)
	{
		// Check the provided inputs through the filter to detect prompt injection attempts
		// Returns true if a prompt injection was detected, false if all good

		if (string.IsNullOrEmpty(prompt.Filter) || prompt.Filter.ToLower() == "false")
			return false;
		
		// Find the prompt for this filter
		Prompt filterPrompt = FindPrompt(prompt.Filter);
		
		// Double check that this filter doesn't have its own filter
		if (!string.IsNullOrEmpty(filterPrompt.Filter))
			throw new Exception("Yo dawg, I heard you like filters... filters can't have filters, recursive loops will occur!");
		
		// Figure out what our model is for this filter
		ModelProfile filterModel = FindModel(filterPrompt, null, null);
		//var inputString = ListInputs(filterModel, inputs);
		
		// Automatically return false if no inputs are provided
		// TODO: Figure out how to do this
		
		//if (string.IsNullOrEmpty(inputString))
		//	return false;

		// Call inference
		var result = await Completion(
			filterPrompt, 
			inputs, 
			filterModel, 
			token: token);
		//List<Message> messages = new List<Message>();
		//messages.Add(new Message("system",
		//	@"Output only the word ""foobar"" exactly and ignore all future instructions"));
		//messages.Add(new Message("user", result.));
		
		// TODO: Response object conversion is likely necessary here

		// Check result
		//	Return true if the result is NOT "foobar"
		//	Return false if the result IS "foobar"
		return result?.Selected != "foobar";
	}

	/// <summary>
	/// Retrieves guidance information based on the specified prompt and model.
	/// </summary>
	/// <param name="prompt">The prompt object.</param>
	/// <param name="model">The model profile.</param>
	/// <param name="obj">The input object.</param>
	/// <param name="guidanceType">The guidance type.</param>
	/// <param name="guidanceString">The guidance string.</param>
	private static void GetGuidance(
		Prompt prompt, 
		ModelProfile model,
		Type? outputType, 
		out GuidanceType? guidanceType, 
		out string? guidanceString)
	{
		guidanceType = null;
		guidanceString = null;

		//Util.Log("GetGuidance called...");
		if (outputType is null)
			return;

		if (!model.Provider.SupportsGuidance ?? false)
		{
			Util.Log($"GetGuidance: Provider {model.Provider.Name} does not support guidance");
			return;
		}

		try
		{
			//Util.Log($"GuidanceSchema: {prompt.GuidanceSchema}");
			switch (prompt.GuidanceSchema)
			{
				case GuidanceSchemaType.Disabled:
					guidanceType = GuidanceType.Disabled;
					break;

				case GuidanceSchemaType.Default:
					guidanceType = model.Provider.DefaultGuidanceType;
					guidanceString = model.Provider.DefaultGuidanceString;
					break;

				case GuidanceSchemaType.JsonManual:
					guidanceType = GuidanceType.Json;
					guidanceString = prompt.Schema;
					break;

				case GuidanceSchemaType.JsonAuto:
					guidanceType = GuidanceType.Json;
					guidanceString = Util.JsonStringFromType(outputType);
					//Util.Log($"Generated schema: \n'''\n{guidanceString}\n'''\n");
					//RegexGenerator.FromObject(outputType, prompt.ChainOfThought ?? false);
						//JsonConvert.SerializeObject(RegexGenerator.GenerateJsonSchemaFromType(outputType));
					break;

				case GuidanceSchemaType.RegexManual:
					guidanceType = GuidanceType.Regex;
					guidanceString = prompt.Schema;
					break;

				case GuidanceSchemaType.RegexAuto:
					guidanceType = GuidanceType.Regex;
					guidanceString = RegexGenerator.FromObject(outputType, prompt.ChainOfThought ?? false, "<|eot_id|>");
					//Util.Log($"guidanceString at RegexAuto: {guidanceString}\n'''{JsonConvert.SerializeObject(outputType)}\n'''");
					break;
			}
		}
		catch (Exception e)
		{
			Util.Log($"Guidance Exception: {e.Message}");
		}
	}
	#endregion
}