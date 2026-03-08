// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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
	private static readonly object LogLock = new object();
	private static readonly SemaphoreSlim DumpLogSemaphore = new SemaphoreSlim(1, 1);
	private static readonly DateTime SessionTime = DateTime.Now;

	public static void LogBuild(StringBuilder sb, string text)
	{
		sb.Append(text);
		Log(text);
	}
	
 public static void Log(string text,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		// If ReviLogger is available via DI, route through it for consistent observability
		if (ReviServiceLocator.TryGetLogger(out IReviLogger? logger) && logger != null)
		{
 		logger.Log(
 			parent: null,
 			LogLevel.Info,
 			message: text,
 			identifier: member, 
 			cycle: 0,
 			tags: $"legacyutil {file}:{member}:{line}",
 			object1: null,
 			object1Name: null,
 			object2: null,
 			object2Name: null,
 			file,
 			member,
 			line);
			return;
		}
		
		bool debug = false;

		lock (LogLock)
		{
			if (debug)
			{
				var message = string.Format("{0}:{1}:{2}: ", 
					Path.GetFileName(file), 
					member, 
					line);
				var originalColor = Console.ForegroundColor;
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(message);
				Console.ForegroundColor = originalColor;
				Console.Write($"{text.Replace("\n", "\n" + message)}\n");
			}
			else
			{
				Console.WriteLine(text.Replace("\\n", "\n"));
			}
		}
	}

	public static async Task DumpRlog(Rlog rlog)
	{
		await DumpLog(rlog.ToString(), rlog.Identifier);
	}
	
 public static async Task DumpLog(StringBuilder sb, string fileNamePrefix)
	{
		if (ReviServiceLocator.TryGetLogger(out IReviLogger? logger) && logger != null)
		{
			await logger.DumpLog(sb, fileNamePrefix);
			return;
		}
		await DumpLog(sb.ToString(), fileNamePrefix);
	}

	/// <summary>
	/// Dump the text into file and save it in format 'prefix-yyyy-MMM-dd-h:mm:sstt.txt' 
	/// inside a 'ResenLogs' folder in user's home directory
	/// </summary>
	/// <param name="textToDump">The text to be saved into the file</param>
	/// <param name="fileNamePrefix">The prefix to the file name</param>
 public static async Task DumpLog(string? textToDump, string fileNamePrefix)
	{
		if (string.IsNullOrEmpty(textToDump))
			return;

		// Prefer ReviLogger if available
		if (ReviServiceLocator.TryGetLogger(out IReviLogger? logger) && logger != null)
		{
			await logger.DumpLog(textToDump, fileNamePrefix);
			return;
		}

		await DumpLogSemaphore.WaitAsync();

		try 
		{
			// Capture stack trace
			var stackTrace = EnhancedStackTrace.Current(); //new StackTrace(true);
			string stackTraceString = $"Stack Trace:\n{stackTrace}";

			// Prepend stack trace to the text
			textToDump = stackTraceString + "\n\n" + textToDump;
			
			var homeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var fileSuffix = "_1";
			var counter = 2;
			
			string fileName;
			string fullFilePath;

			string folder = $"session_{SessionTime:yyyy-MMM-dd_HH-mm-ss}";
			
			do
			{
				// Formatting the filename
				fileName = $"{fileNamePrefix}{fileSuffix}.txt";
				
				// Create the file path
				fullFilePath = Path.Combine(homeFolder, "ResenLogs", folder, fileName);
				
				fileSuffix = "_" + counter++;
				
				if (counter > 100000)
				{
					throw new Exception("Util.LogDump: Too many duplicate file names detected! Failing out...");
				}
			}
			while (File.Exists(fullFilePath));

			// Ensure that the directory exists
			Directory.CreateDirectory(Path.GetDirectoryName(fullFilePath));

			// Writing text to the file
			await File.WriteAllTextAsync(fullFilePath, textToDump);
			//Log($"Util.LogDump: Saved {fileName}");
		}
		catch (Exception ex)
		{
			Log($"DumpLog failed! {ex.Message}");
			throw;
		}
		finally
		{
			DumpLogSemaphore.Release();
		}
	}

	/// <summary>
	/// Dump binary image bytes to the same location/pattern as DumpLog, using provided prefix and image extension.
	/// </summary>
	/// <param name="imageBytes">Binary image content (e.g., PNG bytes)</param>
	/// <param name="fileNamePrefix">Prefix for the dumped image file</param>
	/// <param name="extension">Image extension without dot (defaults to "png")</param>
	public static async Task DumpImage(byte[] imageBytes, string fileNamePrefix, string extension = "png")
	{
		if (imageBytes == null || imageBytes.Length == 0)
			return;

		await DumpLogSemaphore.WaitAsync();
		try
		{
			var homeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var fileSuffix = "_1";
			var counter = 2;
			string fileName;
			string fullFilePath;
			string folder = $"session_{SessionTime:yyyy-MMM-dd_HH-mm-ss}";
			string safeExt = string.IsNullOrWhiteSpace(extension) ? "png" : extension.TrimStart('.');

			do
			{
				fileName = $"{fileNamePrefix}{fileSuffix}.{safeExt}";
				fullFilePath = Path.Combine(homeFolder, "ResenLogs", folder, fileName);
				fileSuffix = "_" + counter++;
				if (counter > 1000)
					throw new Exception("Util.DumpImage: Too many duplicate file names detected! Failing out...");
			}
			while (File.Exists(fullFilePath));

			Directory.CreateDirectory(Path.GetDirectoryName(fullFilePath));
			await File.WriteAllBytesAsync(fullFilePath, imageBytes);
		}
		catch (Exception ex)
		{
			Log($"DumpImage failed! {ex.Message}");
			throw;
		}
		finally
		{
			DumpLogSemaphore.Release();
		}
	}

	public static string PrintObject(object obj)
	{
		return PrintObject(obj, 0);
	}

	private static string PrintObject(object obj, int indentationLevel)
	{
		if (obj == null)
			return "";

		Type type = obj.GetType();
		PropertyInfo[] properties = type.GetProperties();

		string result = "";

		foreach (PropertyInfo property in properties)
		{
			object value = property.GetValue(obj);
			string propertyName = property.Name;

			if (value != null)
			{
				if (value.GetType().IsClass && value.GetType() != typeof(string))
				{
					result += $"{Indent(indentationLevel)}{propertyName}: {PrintObject(value, indentationLevel + 1)}\n";
				}
				else
				{
					result += $"{Indent(indentationLevel)}{propertyName}: {value}\n";
				}
			}
			else
			{
				result += $"{Indent(indentationLevel)}{propertyName}: NULL\n";
			}
		}

		return result;
	}

	private static string Indent(int count)
	{
		return new string(' ', count * 4);
	}
	
	public static void PrintProperties(object myObj)
	{
		foreach(var prop in myObj.GetType().GetProperties())
		{ 
			Console.WriteLine (prop.Name + ": " + prop.GetValue(myObj, null));
		}

		foreach(var field in myObj.GetType().GetFields())
		{ 
			Console.WriteLine (field.Name + ": " + field.GetValue(myObj));
		}
	}
}