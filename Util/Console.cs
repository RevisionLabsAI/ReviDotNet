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

using System.Diagnostics;
using System.Net.Mime;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DeepDev;

namespace Revi;

// This file is bleh, please ignore how bleh this file is. Thank you. :) 

public static partial class RUtil
{
	[GeneratedRegex("^[a-zA-Z0-9\\s\\W]+$")]
	private static partial Regex MatchCharacter();
	
	
	public static void PrintDivider(int count)
	{
		for (int i = 0; i < count; i++)
		{
			Console.Write("=");
		}
		Console.WriteLine();
	}

	public static string ReadHintedLine(
		List<string> list,
		List<string> history,
		string prompt)
	{
		ConsoleKeyInfo input;
		var userInput = string.Empty;
		var readLine = string.Empty;

		var index = -1;

		Console.Write(prompt);

		while ((input = Console.ReadKey()).Key != ConsoleKey.Enter)
		{
			switch (input.Key)
			{
				case ConsoleKey.Backspace:
					userInput = userInput.Any() ? userInput.Remove(userInput.Length - 1, 1) : string.Empty;
					break;

				case ConsoleKey.Delete:
					userInput = string.Empty;
					break;

				case ConsoleKey.Tab:
					//userInput = string.IsNullOrEmpty(suggestion) ? userInput : suggestion;
					string matched = "", lastmatched = "";
					if (!string.IsNullOrEmpty(userInput))
					{
						int count = 0;
						foreach (var item in list)
						{
							if (item.ToString().StartsWith(userInput))
							{
								++count;
								matched += $"\n{item}";
								lastmatched = item;
							}
						}
						if (count == 1)
						{
							userInput = lastmatched;
						}
						else if (!string.IsNullOrEmpty(matched))
						{
							Console.Write(matched + "\n\n");
						}
					}
					break;

				case ConsoleKey.UpArrow:
				{
					if (!history.Any())
						break;

					index = Math.Clamp(index + 1, 0, history.Count - 1);
					userInput = history[Math.Clamp(history.Count - 1 - index, 0, history.Count - 1)];
					break;
				}

				case ConsoleKey.DownArrow:
				{
					if (!history.Any())
						break;

					index = Math.Clamp(index - 1, 0, history.Count - 1);
					userInput = history[Math.Clamp(history.Count - 1 - index, 0, history.Count - 1)];
					break;
				}

				default:
					if (MatchCharacter().IsMatch(input.KeyChar.ToString()))
						userInput += input.KeyChar;
					break;
			}

			// Choose a suggestion
			var suggestion = string.Empty;
			if (!string.IsNullOrEmpty(userInput))
			{
				foreach (var item in list)
				{
					if (item.ToString().StartsWith(userInput))
					{
						suggestion = item;
						break;
					}
				}
			}

			// Set the readline to be the suggestion if there is one
			readLine = string.IsNullOrEmpty(suggestion) ? userInput : suggestion;

			// Start at the beginning of the line
			Console.SetCursorPosition(0, Console.CursorTop);

			// Write the prompt plus the user's current input
			string typedText = prompt + userInput;
			Console.Write($"{typedText.Substring(
				typedText.Length
				-
				Math.Min(
					typedText.Length,
					Console.BufferWidth - 3
				),
				Math.Min(typedText.Length, Console.BufferWidth - 3)
			)}");

			// Add hint text
			var originalColor = Console.ForegroundColor;
			string hintText = readLine.Substring(
				userInput.Length,
				readLine.Length - userInput.Length
			);
			if (userInput.Any())
			{       
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(hintText);
				Console.ForegroundColor = originalColor;
			}

			// Fill the remainder of the line with spaces
			Console.Write(new string(' ',
				Console.BufferWidth - 1
				-
				Math.Min(
					Console.BufferWidth - 1,
					(typedText + hintText).Length)
				));

			// Reset cursor position to where the user is actually typing
			Console.SetCursorPosition(Math.Min(typedText.Length, Console.BufferWidth - 4), Console.CursorTop);
		}

		Console.WriteLine($"{prompt}{readLine}");

		return userInput.Any() ? readLine : string.Empty;
	}
}