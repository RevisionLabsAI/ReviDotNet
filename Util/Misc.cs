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
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DeepDev;

namespace Revi;

// This file is bleh, please ignore how bleh this file is. Thank you. :) 

public static partial class Util
{
	public static string Identifierize(string input)
	{
		// Remove non-alphanumeric characters
		var alphanumericPattern = new Regex("[^a-zA-Z0-9 -]");
		input = alphanumericPattern.Replace(input, "");

		// Trim whitespace and replace spaces with dashes
		return input.Trim().Replace(' ', '-');
	}
	
	// Helper for getting a string value safely
	public static string? GetDictionaryString(Dictionary<string, string> dictionary, string key, string? defaultValue = "")
	{
		if (dictionary.TryGetValue(key, out string value))
			return value;
		return defaultValue;
	}

	// Helper for getting an int value safely
	public static int? GetDictionaryInt(Dictionary<string, string> dictionary, string key, int? defaultValue = 0)
	{
		if (dictionary.TryGetValue(key, out string value) && int.TryParse(value, out int result))
			return result;
		return defaultValue;
	}

	// Helper for getting a DateTime value safely
	public static DateTime? GetDictionaryDateTime(Dictionary<string, string> dictionary, string key, DateTime? defaultValue = default(DateTime?))
	{
		if (dictionary.TryGetValue(key, out string value) && DateTime.TryParse(value, out DateTime result))
			return result;
		return defaultValue;
	}

	// Helper for getting a bool value safely
	public static bool? GetDictionaryBool(Dictionary<string, string> dictionary, string key, bool? defaultValue = false)
	{
		if (dictionary.TryGetValue(key, out string value) && bool.TryParse(value, out bool result))
			return result;
		return defaultValue;
	}
	
	public static bool GetDictionaryBool(Dictionary<string, string> dictionary, string key, bool defaultValue = false)
	{
		if (dictionary.TryGetValue(key, out string value) && bool.TryParse(value, out bool result))
			return result;
		return defaultValue;
	}

	// Helper for getting a nullable float value safely
	public static float? GetDictionaryFloat(Dictionary<string, string> dictionary, string key, float? defaultValue = 0)
	{
		if (dictionary.TryGetValue(key, out string value) && float.TryParse(value, out float result))
			return result;
		return defaultValue;
	}
	
	public static float CosineSimilarity(string str1, string str2)
	{
		var vector1 = GetCharacterFrequencyVector(str1);
		var vector2 = GetCharacterFrequencyVector(str2);

		float dotProduct = DotProduct(vector1, vector2);
		float magnitude1 = Magnitude(vector1);
		float magnitude2 = Magnitude(vector2);

		if (magnitude1 == 0f || magnitude2 == 0f)
		{
			return 0f;
		}

		return dotProduct / (magnitude1 * magnitude2);
	}
	
	public static string ExtractSubDirectories(string basePath, string completePath)
	{
		if (!completePath.StartsWith(basePath))
		{
			throw new ArgumentException("The provided path does not start with the base directory");
		}
        
		string relativePath = completePath.Substring(basePath.Length);
		string[] parts = relativePath.Split(new[] {'/', '\\'}, StringSplitOptions.RemoveEmptyEntries);
		string directories = "";

		foreach (var part in parts)
		{
			if (!Path.HasExtension(part))
			{
				directories += part.Replace('\\', '/') + "/";
			}
			else
			{
				break;
			}
		}
		return directories;
	}

	private static Dictionary<char, int> GetCharacterFrequencyVector(string str)
	{
		Dictionary<char, int> frequencyVector = new Dictionary<char, int>();
		foreach (char ch in str)
		{
			if (frequencyVector.ContainsKey(ch))
				frequencyVector[ch]++;
			else
				frequencyVector[ch] = 1;
		}
		return frequencyVector;
	}

	private static float DotProduct(Dictionary<char, int> vector1, Dictionary<char, int> vector2)
	{
		float dotProduct = 0f;
		foreach (var pair in vector1)
		{
			int value;
			if (vector2.TryGetValue(pair.Key, out value))
			{
				dotProduct += pair.Value * value;
			}
		}
		return dotProduct;
	}

	private static float Magnitude(Dictionary<char, int> vector)
	{
		float sum = 0f;
		foreach (int value in vector.Values)
		{
			sum += value * value;
		}
		return (float)Math.Sqrt(sum);
	}

	public static string ExtractDomainName(string url)
	{
		if (!url.StartsWith("http://") && !url.StartsWith("https://"))
			url = "http://" + url;
    
		var uri = new Uri(url);
		return uri.Host;
	}
	
	public static string DiffStrings(string original, string modified)
	{
		StringBuilder result = new StringBuilder();
		int minLen = Math.Min(original.Length, modified.Length);

		// Compare characters and note changes.
		for (int i = 0; i < minLen; i++)
		{
			if (original[i] == modified[i])
			{
				result.Append(original[i]); // Character unchanged
			}
			else
			{
				int start = i;
				// Find the end of the differing segment
				while (i < minLen && original[i] != modified[i])
				{
					i++;
				}
				result.AppendFormat("[-{0}+{1}]", original.Substring(start, i - start), modified.Substring(start, i - start));
				i--; // decrement i because the for loop will increment it
			}
		}

		// If one string is longer than the other, add the remaining characters.
		if (original.Length > modified.Length)
		{
			result.AppendFormat("[-{0}]", original.Substring(minLen));
		}
		else if (modified.Length > original.Length)
		{
			result.AppendFormat("[+{0}]", modified.Substring(minLen));
		}

		return result.ToString();
	}

	
    /// <summary>
    /// Basically the same as Lazy with LazyThreadSafetyMode of ExecutionAndPublication, BUT exceptions are not cached
    /// </summary>
    // I don't actually understand how this operates, but the internet says it's a good idea. 
    public class LazyWithNoExceptionCaching<T>
	{
		private Func<T> valueFactory;
		private T value = default;
		private readonly object lockObject = new object();
		private bool initialized = false;
		private static readonly Func<T> ALREADY_INVOKED_SENTINEL = () => default;

		public LazyWithNoExceptionCaching(Func<T> valueFactory)
		{
			this.valueFactory = valueFactory;
		}

		public bool IsValueCreated
		{
			get { return initialized; }
		}

		public T Value
		{
			get
			{
				// Mimic LazyInitializer.EnsureInitialized()'s double-checked locking, whilst
				// allowing control flow to clear valueFactory on successful initialisation.
				if (Volatile.Read(ref initialized))
					return value;

				lock (lockObject)
				{
					if (Volatile.Read(ref initialized))
						return value;

					value = valueFactory();
					Volatile.Write(ref initialized, true);
				}
				valueFactory = ALREADY_INVOKED_SENTINEL;
				return value;
			}
		}
	}

	public static async Task<TResult> TimeoutAfter<TResult>(this Task<TResult> task, TimeSpan timeout) 
	{
		using (var timeoutCancellationTokenSource = new CancellationTokenSource()) 
		{
			var completedTask = await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token));
			if (completedTask == task) 
			{
				timeoutCancellationTokenSource.Cancel();
				return await task;  // Very important in order to propagate exceptions
			} else {
				throw new TimeoutException("The operation has timed out.");
			}
		}
	}
}