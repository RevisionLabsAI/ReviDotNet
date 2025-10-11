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

using System.Diagnostics;
using System.Net.Mime;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DeepDev;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Revi;

public class LogService : ILogService
{
	private static readonly object LogLock = new object();
	private static readonly SemaphoreSlim DumpLogSemaphore = new SemaphoreSlim(1, 1);
	private static readonly DateTime SessionTime = DateTime.Now;
	
	private readonly ILogEventPublisher? _eventPublisher;
	
	public LogLevel DefaultLogLevel { get; }
	
	public LogService(ILogEventPublisher? eventPublisher = null, LogLevel defaultLogLevel = LogLevel.Debug)
	{
		_eventPublisher = eventPublisher;
		DefaultLogLevel = defaultLogLevel;
	}

	public Record LogInfo(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent: null, 
			LogLevel.Info, 
			message, 
			identifier, 
			cycle, 
			tags, 
			object1, 
			object2, 
			file, 
			member, 
			line);
	}
	
	public Record LogInfo(
		Record parent, 
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent, 
			LogLevel.Info, 
			message, 
			identifier, 
			cycle, 
			tags, 
			object1, 
			object2, 
			file, 
			member, 
			line);
	}

	public Record LogDebug(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent: null,
			LogLevel.Debug,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
		
	}
	
	public Record LogDebug(
		Record parent, 
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent,
			LogLevel.Debug,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
	}
	
	public Record LogWarning(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent: null,
			LogLevel.Warning,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
	}
	
	public Record LogWarning(
		Record parent, 
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent,
			LogLevel.Warning,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
	}
	
	public Record LogError(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent: null,
			LogLevel.Error,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
	}
	
	public Record LogError(
		Record parent, 
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent,
			LogLevel.Error,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
	}
	
	public Record LogFatal(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent: null,
			LogLevel.Fatal,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
	}

	public Record LogFatal(
		Record parent,
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		return Log(
			parent,
			LogLevel.Fatal,
			message,
			identifier,
			cycle,
			tags,
			object1,
			object2,
			file,
			member,
			line);
	}

	public Record Log(
		Record? parent,
		LogLevel level,
		string message, 
		string? identifier = "", 
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		// Traverse through all parents and append to StringBuilder as appropriate
		// Optimized to avoid unnecessary traversal if no parent has a builder
		if (parent?.Builder != null || parent?.Parent != null)
		{
			Record? selected = parent;
			while (selected is not null)
			{
				selected.Builder?.AppendLine(message);
				selected = selected.Parent; 
			}
		}
		
		// Print if log level is enabled
		if (level >= DefaultLogLevel)
		{
			try
			{
				Console.WriteLine(message);
			}
			catch
			{
				// Silently ignore console write failures to prevent logging from crashing the application
			}
		}

		// Create the Record first to get its Id
		Record record = new(
			parent, 
			level, 
			message, 
			identifier, 
			cycle, 
			tags, 
			object1, 
			object2, 
			file ?? "", 
			member ?? "", 
			line ?? 0);

		// Only create and publish LogEvent if there's an event publisher configured
		if (_eventPublisher != null)
		{
			try
			{
				_eventPublisher.PublishLogEvent(new LogEvent
				{
					Id = record.Id,
					ParentId = parent?.Id,
					Timestamp = DateTime.UtcNow,
					Level = level,
					Message = message,
					Identifier = identifier,
					Cycle = cycle,
					Tags = tags,
					Object1 = object1 != null ? JsonConvert.SerializeObject(object1, Formatting.Indented, new StringEnumConverter()) : null,
					Object2 = object2 != null ? JsonConvert.SerializeObject(object2, Formatting.Indented, new StringEnumConverter()) : null,
					File = file,
					Member = member,
					Line = line
				});
			}
			catch
			{
				// Silently ignore event publishing failures to prevent logging from crashing the application
			}
		}

		return record;
	}
	
	public async Task DumpLog(StringBuilder sb, string fileNamePrefix)
	{
		await DumpLog(sb.ToString(), fileNamePrefix);
	}

	/// <summary>
	/// Dump the text into file and save it in format 'prefix-yyyy-MMM-dd-h:mm:sstt.txt' 
	/// inside a 'ResenLogs' folder in user's home directory
	/// </summary>
	/// <param name="textToDump">The text to be saved into the file</param>
	/// <param name="fileNamePrefix">The prefix to the file name</param>
	public async Task DumpLog(string? textToDump, string fileNamePrefix)
	{
		if (string.IsNullOrEmpty(textToDump))
			return;

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
			Util.Log($"DumpLog failed! {ex.Message}");
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
	public async Task DumpImage(byte[] imageBytes, string fileNamePrefix, string extension = "png")
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
			Util.Log($"DumpImage failed! {ex.Message}");
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