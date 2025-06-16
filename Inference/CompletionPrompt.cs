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

using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Revi;

public static class CompletionPrompt
{
	// ================
	//  Prompt Builder
	// ================

	#region Prompt Builder
	/// <summary>
	/// Builds a prompt string using the given prompt, model profile, and inputs.
	/// </summary>
	/// <param name="prompt">The prompt object to build the string from.</param>
	/// <param name="model">The model profile to use for building the string.</param>
	/// <param name="inputs">Optional list of inputs to include in the string.</param>
	/// <returns>The resulting string built from the prompt, model profile, and inputs.</returns>
	public static string BuildString(Prompt prompt, ModelProfile model, List<Input>? inputs = null)
	{
		/*
	       Structure = "{header}{body}{example}{input}{output}",
	       HeaderSection = "## Instructions\n{content}\n\n",
	       BodySection = "## Task\n{content}\n\n",
	       InputSection = "## Input\n{list}\n\n",
	       InputItem = "{iterator}. {content}\n",
	       OutputSection = "## Output\n"
		 */
		
		// Start with the structure
		if (string.IsNullOrEmpty(model.Structure))
			throw new Exception($"Invalid model profile for a completion prompt - missing structure!");
		
		// Process inputs
		ProcessInputs(
			prompt, 
			model, 
			out string systemSection, 
			out string instructionSection, 
			out string inputSection, 
			inputs);

		string exampleSection = ListExamples(prompt, model);
		//Util.Log($"exampleSection: \n'''\n{exampleSection}\n'''\n");
		
		string output = model.Structure;
		output = ContentReplace(
			"system", 
			output, 
			model.SystemSection ?? "",
			systemSection);
		
		output = ContentReplace(
			"instruction", 
			output, 
			model.InstructionSection ?? "", 
			instructionSection);
		
	    output = ContentReplace(
		    "input", 
		    output, 
		    model.InputSection ?? "",
		    inputSection);
	    
	    output = ContentReplace(
		    "example", 
		    output,
		    model.ExampleSection ?? "",
		    exampleSection);
	    
	    output = output.Replace("{output}", model.OutputSection);
		return output;
	}
	#endregion
	
	
	// ================
	//  Input Handling
	// ================

	#region Input Handling
	/// <summary>
	/// Adds or fills the input based on the specified input type.
	/// </summary>
	/// <param name="inputType">The type of input to handle.</param>
	/// <param name="inputList">The input list to add or fill.</param>
	/// <param name="inputs">The list of inputs.</param>
	/// <param name="originalText">The original text to add or fill the input into.</param>
	/// <returns>The updated text after adding or filling the input.</returns>
	private static string AddOrFillInput(
		InputType inputType, 
		string? inputList, 
		List<Input> inputs, 
		string originalText)
	{
		switch (inputType)
		{
			case InputType.Listed:
				if (!string.IsNullOrEmpty(inputList))
				{
					if (string.IsNullOrEmpty(originalText))
						originalText = inputList;
					else
						originalText += ("\n" + inputList);
				}
				break;
			
			case InputType.Filled:
				foreach (Input input in inputs)
				{
					originalText = originalText.Replace("{" + input.Identifier + "}", input.Text);
				}
				break;
		}

		return originalText;
	}

	/// <summary>
	/// Process the inputs for building chat messages.
	/// </summary>
	/// <param name="prompt">The prompt object containing system and instruction.</param>
	/// <param name="model">The model profile used for processing inputs.</param>
	/// <param name="systemSection">The output system string after processing inputs.</param>
	/// <param name="instructionSection">The output instruction string after processing inputs.</param>
	/// <param name="inputSection">The output input string after processing inputs.</param>
	/// <param name="inputs">The list of inputs used for replacing placeholders in the prompt. (optional)</param>
	private static void ProcessInputs(
		Prompt prompt,
		ModelProfile model,
		out string systemSection,
		out string instructionSection,
		out string inputSection,
		List<Input>? inputs = null)
	{
		// Process inputs
		// TODO: Add checks for inputs
		//  - Error if the formatting is filled but label is empty
		//  - Error if the label is one of the core labels
		systemSection = new string(prompt.System ?? "");
		instructionSection = new string(prompt.Instruction ?? "");
		inputSection = "";

		if (inputs is null || !inputs.Any())
			return;

		// Generate the inputList if we're going to need it
		string? inputList = "";
		
		// Create the inputSection if one of these items requests it
		if (model.SystemInputType == InputType.Listed || model.InstructionInputType == InputType.Listed)
		{
			
			inputList = Infer.ListInputs(model, inputs);
			inputSection = AddOrFillInput(InputType.Listed, inputList, inputs, "");
		}
		
		// Fill the inputs for the system prompt if applicable
		systemSection = AddOrFillInput(
			model.SystemInputType == InputType.Filled ? InputType.Filled : InputType.None, 
			inputList, 
			inputs, 
			systemSection);
		
		// Fill the inputs for the instruction prompt if applicable
		instructionSection = AddOrFillInput(
			model.InstructionInputType == InputType.Filled ? InputType.Filled : InputType.None, 
			inputList, 
			inputs, 
			instructionSection);
	}
	#endregion
	
	
	// ==================
	//  Example Handling
	// ==================
	
	#region Example Handling
	// Formatting guide: 
	// Example section:                ## Examples\n{content}
	// Example structure:              Example #{iterator}:\n{exsystem}{exinstruction}{exinput}{exoutput}\n
	// Example subheader system:       - System (#{iterator}):\n{content}\n\n
	// Example subheader instruction:  - Instruction (#{iterator}):\n{content}\n\n
	// Example subheader input:        - Input (#{iterator}):\n{content}\n\n
	// Example subheader output:       - Output (#{iterator}):\n{content}\n\n

	private static string IndentLines(string text)
	{
		string[] lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
		for (int i = 0; i < lines.Length; i++) {
			lines[i] = "  " + lines[i];  // Add two spaces to each line.
		}
		text = string.Join(Environment.NewLine, lines);
		return text;
	}

	/// <summary>
	/// Inserts examples into the list of messages.
	/// </summary>
	/// <param name="prompt">The prompt object that contains the examples.</param>
	/// <param name="model">The model profile.</param>
	/// <returns>The output string with the examples inserted.</returns>
	private static string ListExamples(
		Prompt prompt,
		ModelProfile model)
	{
		if ((prompt.Examples is null) || !prompt.Examples.Any())
			return "";
		
		int maxExamples = Math.Min(prompt.FewShotExamples ?? 0, prompt.Examples.Count);
		
		if (maxExamples == 0)
			return "";
		
		if (string.IsNullOrEmpty(model.ExampleSection) || string.IsNullOrEmpty(model.ExampleStructure))
			return "";
		
		string list = "";
		for (int index = 0; index < maxExamples; ++index)
		{
			// TODO: Detect invalid example with missing input or output?
			// maxExamples = Math.Min(maxExamples + 1, prompt.Examples.Count);

			string example = model.ExampleStructure;
			
			// Figure out the system, instruction, and input messages given the prompt, inputs, and model template
			ProcessInputs(
				prompt, 
				model, 
				out string systemExample, 
				out string instructionExample, 
				out string inputExample,
				prompt.Examples[index].Inputs);
			
			//Util.Log($"inputExample: \n'''\n{inputExample}\n'''\n");
			
			// If the inputs are filled and the model template is there, show instruction and system messages.
			// Otherwise, no system/instruction messages included. 
			if (model.SystemInputType != InputType.Filled && model.InstructionInputType != InputType.Filled)
			{
				systemExample = "";
				instructionExample = "";
			}
			
			// Add the iterator
			example = example.Replace("{iterator}", (index + 1).ToString());

			// Insert system message if applicable
			if (!string.IsNullOrEmpty(systemExample))
				systemExample = IndentLines(systemExample);//"  " + systemExample.Replace("\n", "\n  ");
			
			example = ContentReplaceWithIterator(
				"exsystem",
				example,
				model.ExampleSubSystem ?? "",
				(index + 1).ToString(),
				systemExample);

			// Insert instruction message if applicable
			if (!string.IsNullOrEmpty(instructionExample))
				instructionExample = IndentLines(instructionExample); //"  " + instructionExample.Replace("\n", "\n  ");
			
			example = ContentReplaceWithIterator(
				"exinstruction",
				example,
				model.ExampleSubInstruction ?? "",
				(index + 1).ToString(),
				instructionExample);

			// Insert input list
			if (!string.IsNullOrEmpty(inputExample))
				inputExample = IndentLines(inputExample);//"  " + inputExample.Replace("\n", "\n  ");
			
			example = ContentReplaceWithIterator(
				"exinput",
				example,
				model.ExampleSubInput ?? "",
				(index + 1).ToString(),
				inputExample);

			// Insert output example
			string outputExample = prompt.Examples[index].Output;
			if (!string.IsNullOrEmpty(outputExample))
				outputExample = IndentLines(outputExample);//"  " + outputExample.Replace("\n", "\n  ");
			
			example = ContentReplaceWithIterator(
				"exoutput",
				example,
				model.ExampleSubOutput ?? "",
				(index + 1).ToString(),
				outputExample);

			// Add the example text to the list of examples
			list += example;
		}

		return list;
	}
	#endregion
	
	
	// ======================
	//  Supporting Functions
	// ======================

	#region Supporting Functions
	/// <summary>
	/// Replaces content in the baseText using the given identifier and content.
	/// </summary>
	/// <param name="identifier">The identifier to replace in the baseText.</param>
	/// <param name="baseText">The base text where replacement will occur.</param>
	/// <param name="header">The header to insert in place of the identifier.</param>
	/// <param name="content">The content to insert in the header.</param>
	/// <returns>The modified baseText with the replacement.</returns>
	private static string ContentReplace(
		string identifier, 
		string baseText, 
		string header, 
		string content)
	{
		var matches = Regex.Matches(baseText, @$"({{{identifier}}})").Count;
		switch (matches)
		{
			case 0:
				// Actually, this is fine/normal. 
				return "";
			
			case > 1:
				throw new Exception(
					"Invalid model, prompt, or prompt injection: Too many identifiers wanting replacement.");

			default:
			{
				string insertedText;
				if (string.IsNullOrEmpty(content))
					insertedText = "";
				else
					insertedText = header.Replace("{content}", content);
				
				return baseText.Replace($"{{{identifier}}}", insertedText);
			}
		}
	}

	/// <summary>
	/// Replaces content with iterator.
	/// </summary>
	/// <param name="identifier">The identifier to be replaced in the baseText.</param>
	/// <param name="baseText">The base text containing the identifier to be replaced.</param>
	/// <param name="header">The header template.</param>
	/// <param name="iterator">The iterator value.</param>
	/// <param name="content">The content to be inserted.</param>
	/// <returns>The baseText with the identifier replaced by the content and iterator.</returns>
	private static string ContentReplaceWithIterator(
		string identifier,
		string baseText, 
		string header, 
		string iterator,
		string content)
	{
		var matches = Regex.Matches(baseText, @$"({{{identifier}}})").Count;
		switch (matches)
		{
			case 0:
				// Actually, this is fine/normal. 
				return "";
			
			case > 1:
				throw new Exception(
					"Invalid model, prompt, or prompt injection: Too many identifiers wanting replacement.");

			default:
			{
				string insertedText;
				if (string.IsNullOrEmpty(content))
					insertedText = "";
				else
				{
					insertedText = header.Replace("{iterator}", iterator);
					insertedText = insertedText.Replace("{content}", content);
				}

				return baseText.Replace($"{{{identifier}}}", insertedText);
		}
		}
	}
	#endregion
}
