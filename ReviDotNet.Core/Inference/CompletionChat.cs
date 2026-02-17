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

namespace Revi;

public static class CompletionChat
{
	// =================
	//  Message Builder
	// =================

	#region Message Builder
	/// <summary>
	/// Builds a list of messages for the completion chat.
	/// </summary>
	/// <param name="prompt">The prompt to use for the messages.</param>
	/// <param name="model">The model profile to use for the messages.</param>
	/// <param name="inputs">Optional list of inputs for the messages.</param>
	/// <returns>A list of messages for the completion chat.</returns>
	public static List<Message> BuildMessages(Prompt prompt, ModelProfile model, List<Input>? inputs = null)
	{
		// Process inputs
		ProcessInputs(prompt, model, out string system, out string instruction, inputs);
		
		// Figure out whether we should do example messages first
		//  - Are examples part of the user message, or are they separate user messages?
		//  - Right now examples are separate messages... maybe support making them part of the prompt?
		
		// Form the prompt
		List<Message> messages = new List<Message>();
		InsertSystem(messages, model, system, instruction);
		InsertExamples(messages, prompt, model);
		InsertPrompt(messages, model, system, instruction);

		// Check if something is screwy
		if (messages.Count is 0)
		{
			// Something is indeed screwy
			throw new Exception("No messages for inference");
		}
		
		return messages;
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
			case InputType.Both:
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
	/// <param name="system">The output system string after processing inputs.</param>
	/// <param name="instruction">The output instruction string after processing inputs.</param>
	/// <param name="inputs">The list of inputs used for replacing placeholders in the prompt.</param>
	private static void ProcessInputs(
		Prompt prompt, 
		ModelProfile model,
		out string system, 
		out string instruction, 
		List<Input>? inputs = null)
	{
		// Process inputs
		// TODO: Add checks for inputs
		//  - Error if the formatting is filled but label is empty
		//  - Error if the label is one of the core labels
		system = new string (prompt.System ?? "");
		instruction = new string(prompt.Instruction ?? "");
		
		if (inputs is null || !inputs.Any())
			return;

		// Create the inputSection if one of these items requests it
		InputType systemInputType = prompt.SystemInputTypeOverride ?? model.DefaultSystemInputType;
		InputType instructionInputType = prompt.InstructionInputTypeOverride ?? model.DefaultInstructionInputType;

		// Handle "Both" input type or standard "Filled"
		// We fill first to see what's left
		List<Input> listedInputs = [.. inputs];
		
		if (systemInputType == InputType.Filled || systemInputType == InputType.Both)
		{
			foreach (Input input in inputs)
			{
				string placeholder = "{" + input.Identifier + "}";
				if (system.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
				{
					system = Regex.Replace(system, Regex.Escape(placeholder), input.Text, RegexOptions.IgnoreCase);
					if (systemInputType == InputType.Both)
					{
						listedInputs.Remove(input);
					}
				}
			}
		}

		if (instructionInputType == InputType.Filled || instructionInputType == InputType.Both)
		{
			foreach (Input input in inputs)
			{
				string placeholder = "{" + input.Identifier + "}";
				if (instruction.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
				{
					instruction = Regex.Replace(instruction, Regex.Escape(placeholder), input.Text, RegexOptions.IgnoreCase);
					if (instructionInputType == InputType.Both)
					{
						listedInputs.Remove(input);
					}
				}
			}
		}

		// Generate the inputList if we're going to need it
		string? inputList = "";
		if (systemInputType == InputType.Listed || instructionInputType == InputType.Listed || 
		    systemInputType == InputType.Both || instructionInputType == InputType.Both)
		{
			if (listedInputs.Any())
			{
				inputList = Infer.ListInputs(model, listedInputs);
			}
		}
		
		// Add the inputList if applicable (only if it was requested as Listed)
		if (systemInputType == InputType.Listed)
			system = AddOrFillInput(systemInputType, inputList, inputs, system);
		
		if (instructionInputType == InputType.Listed)
			instruction = AddOrFillInput(instructionInputType, inputList, inputs, instruction);
		
		// For Both, we might need to add the inputList too if it's not empty
		if (systemInputType == InputType.Both && !string.IsNullOrEmpty(inputList))
			system = AddOrFillInput(InputType.Listed, inputList, listedInputs, system);

		if (instructionInputType == InputType.Both && !string.IsNullOrEmpty(inputList))
			instruction = AddOrFillInput(InputType.Listed, inputList, listedInputs, instruction);
	}
	#endregion
	
	
	// ==================
	//  Example Handling
	// ==================

	#region Example Handling
	/// <summary>
	/// Inserts examples into the list of messages.
	/// </summary>
	/// <param name="messages">The list of messages to insert the examples into.</param>
	/// <param name="prompt">The prompt object that contains the examples.</param>
	/// <param name="model">The model profile.</param>
	private static void InsertExamples(
		List<Message> messages, 
		Prompt prompt, 
		ModelProfile model)
	{
		if ((prompt.Examples is null) || !prompt.Examples.Any())
		{
			return;
		}
		
		int maxExamples = Math.Min(prompt.FewShotExamples ?? 0, prompt.Examples.Count);

		for (int index = 0; index < maxExamples; ++index)
		{
			string? userMessage = null;
			ProcessInputs(prompt, model, out string system, out string instruction, prompt.Examples[index].Inputs);
			
			// Add system if applicable
			if (model.SystemInUser && string.IsNullOrEmpty(system) is false)
			{
				userMessage += system;
			}
			
			// Add instruction if applicable
			if (model.PromptInUser && string.IsNullOrEmpty(instruction) is false)
			{
				userMessage += instruction;
			}

			if (string.IsNullOrEmpty(userMessage))
				throw new Exception("Null or empty input string when creating examples");
			
			messages.Add(new Message("user", userMessage));
			messages.Add(new Message("assistant", prompt.Examples[index].Output));
		}
	}
	#endregion
	
	
	// =========================
	//  System Message Handling
	// =========================

	#region System Message Handling
	/// <summary>
	/// Inserts a system message into the list of messages.
	/// </summary>
	/// <param name="messages">The list of messages to insert the system message into.</param>
	/// <param name="model">The model profile that determines the behavior of the system message.</param>
	/// <param name="system">The system message to insert (optional).</param>
	/// <param name="instruction">The instruction message to insert (optional).</param>
	private static void InsertSystem(
		List<Message> messages, 
		ModelProfile model, 
		string? system = null,
		string? instruction = null)
	{
		if (model.SystemMessage is false)
			return;
		
		// Add system message if it exists
		string message = "";
		if (!string.IsNullOrEmpty(system))
			message += system;

		// Add the instructions here if we're supposed to
		// If no system message exists but an instruction message exists and we're supposed to add it then we'll add it
		if (string.IsNullOrEmpty(instruction) is false && model.PromptInSystem)
		{
			if (string.IsNullOrEmpty(message))
			{
				message = instruction;
			}
			else
			{
				message += ("\n" + instruction);
			}
		}
		
		// Now add the message to the list of messages
		messages.Add(new Message("system", message));
	}
	#endregion
	
	
	// =====================================
	//  Instruction (User) Message Handling
	// =====================================

	#region User Message Handling
	/// <summary>
	/// Inserts the prompt message into the list of messages.
	/// </summary>
	/// <param name="messages">The list of messages.</param>
	/// <param name="model">The model profile.</param>
	/// <param name="system">The system prompt (optional).</param>
	/// <param name="instruction">The user instruction (optional).</param>
	private static void InsertPrompt(
		List<Message> messages, 
		ModelProfile model, 
		string? system = null,
		string? instruction = null)
	{
		string prompt = "";
		
		// Add the system prompt here if we're supposed to
		if (string.IsNullOrEmpty(instruction) is false && model.SystemInUser)
		{
			prompt += system;
		}

		// Add the instructions here if we're supposed to
		if (string.IsNullOrEmpty(instruction) is false && model.PromptInUser)
		{
			prompt += instruction;
		}
		
		// Now add the message to the list of messages
		if (!string.IsNullOrEmpty(prompt))
			messages.Add(new Message("user", prompt));
	}
	#endregion
}
