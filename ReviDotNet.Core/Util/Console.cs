// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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

public static partial class Util
{
    /// <summary>
    /// Compiled regular expression for validating acceptable key characters.
    /// This replaces the source-generated regex to ensure compatibility across
    /// environments where the GeneratedRegex source generator may not be active.
    /// </summary>
    private static readonly Regex MatchCharacterRegex = new Regex(
        "^[a-zA-Z0-9\\s\\W]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Gets the compiled regex used to validate a single key character.
    /// </summary>
    /// <returns>
    /// A <see cref="Regex"/> instance that matches allowed characters.
    /// </returns>
    private static Regex MatchCharacter()
    {
        return MatchCharacterRegex;
    }
	
	
	public static void PrintDivider(int count)
	{
		for (int i = 0; i < count; i++)
		{
			Console.Write("=");
		}
		Console.WriteLine();
	}

    /// <summary>Reads a line of text, requiring a value when requested.</summary>
    /// <param name="prompt">The text displayed before each read.</param>
    /// <param name="required">Whether an empty answer is rejected.</param>
    /// <returns>The answer, or an empty string when optional input ends.</returns>
    /// <exception cref="EndOfStreamException">Required input ended before an answer was supplied.</exception>
    public static string ReadOpenEnded(string prompt, bool required = false)
    {
        while (true)
        {
            Console.WriteLine(prompt);
            string? input = Console.ReadLine();
            if (input is null)
            {
                if (required) throw new EndOfStreamException("Input ended before the required answer was supplied.");
                return string.Empty;
            }
            if (input.Length > 0 || !required) return input;
        }
    }

    /// <summary>Reads a confirmation. Closed input always declines; only a submitted blank line uses the recommendation.</summary>
    /// <param name="prompt">The question displayed before each read.</param>
    /// <param name="recommendation">The default for a submitted blank line, or null to require an explicit answer.</param>
    /// <returns>True for an affirmative answer; false for a negative answer or closed input.</returns>
    public static bool ReadYesNo(string prompt, bool? recommendation)
    {
        string suffix = recommendation switch { true => " (Y/n):", false => " (y/N):", null => " (y/n):" };
        while (true)
        {
            Console.WriteLine(prompt + suffix);
            string? input = Console.ReadLine();
            if (input is null) return false;
            if (string.IsNullOrWhiteSpace(input) && recommendation.HasValue) return recommendation.Value;
            switch (input.Trim().ToLowerInvariant())
            {
                case "yes": case "y": case "true": return true;
                case "no": case "n": case "false": return false;
            }
        }
    }

    /// <summary>Reads an integer, using the default for blank, declined or closed input.</summary>
    /// <param name="prompt">The text displayed before each read.</param>
    /// <param name="defaultValue">The value for a blank answer, a decline, or closed input.</param>
    /// <returns>The submitted integer or the default value.</returns>
    public static int ReadInt(string prompt, int defaultValue = 0)
    {
        while (true)
        {
            Console.WriteLine(prompt);
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input) || input.Equals("n", StringComparison.OrdinalIgnoreCase) || input.Equals("no", StringComparison.OrdinalIgnoreCase)) return defaultValue;
            if (int.TryParse(input, out int result)) return result;
            Console.WriteLine("Invalid input. Please enter a valid number, or leave blank for the default.");
        }
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