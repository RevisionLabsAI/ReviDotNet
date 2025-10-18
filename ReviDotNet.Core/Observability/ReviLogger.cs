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
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Revi.Configuration;

namespace Revi;

public class ReviLogger : IReviLogger
{
	private static readonly object LogLock = new object();
	private static readonly SemaphoreSlim DumpLogSemaphore = new SemaphoreSlim(1, 1);
	private static readonly DateTime SessionTime = DateTime.Now;
	
	private readonly IRlogEventPublisher? _eventPublisher;
	private readonly RlogConfiguration _rlogConfig;
	
	// Overridable category/type name used for prefix formatting; generic logger overrides this
	protected virtual string? CategoryName => null;
	
	public ReviLogger(
		IRlogEventPublisher eventPublisher,
		IConfiguration configuration)
	{
		_eventPublisher = eventPublisher;
		_rlogConfig = configuration.GetSection("ReviLogger").Get<RlogConfiguration>() ?? GetDefaultRlogConfiguration();
	}

	/// <summary>
	/// Gets the default Rlog configuration
	/// </summary>
	private static RlogConfiguration GetDefaultRlogConfiguration()
	{
		return new RlogConfiguration
		{
			Debug = new RlogLevelConfiguration { PrefixColor = "Green", TextColor = "Gray", ConsolePrint = true },
			Info = new RlogLevelConfiguration { PrefixColor = "Blue", TextColor = "White", ConsolePrint = true },
			Warning = new RlogLevelConfiguration { PrefixColor = "Yellow", TextColor = "White", ConsolePrint = true },
			Error = new RlogLevelConfiguration { PrefixColor = "DarkYellow", TextColor = "DarkYellow", ConsolePrint = true },
			Fatal = new RlogLevelConfiguration { PrefixColor = "Red", TextColor = "Red", ConsolePrint = true }
		};
	}

	public Rlog LogInfo(
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
	
	public Rlog LogInfo(
		Rlog parent, 
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

	public Rlog LogDebug(
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
	
	public Rlog LogDebug(
		Rlog parent, 
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
	
	public Rlog LogWarning(
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
	
	public Rlog LogWarning(
		Rlog parent, 
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
	
	public Rlog LogError(
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
	
	public Rlog LogError(
		Rlog parent, 
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
	
	public Rlog LogFatal(
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

	public Rlog LogFatal(
		Rlog parent,
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

	public Rlog Log(
		Rlog? parent,
		LogLevel level,
		string message, 
		string? identifier = "", 
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		string? file = "",
		string? member = "",
		int? line = 0)
	{
		// Traverse through all parents and append to StringBuilder as appropriate
		// Optimized to avoid unnecessary traversal if no parent has a builder
		if (parent?.Builder != null || parent?.Parent != null)
		{
			Rlog? selected = parent;
			while (selected is not null)
			{
				selected.Builder?.AppendLine(message);
				selected = selected.Parent; 
			}
		}
		
		// Optionally prefix message with type/caller info
		string consoleMessage = message;
		string caller = string.IsNullOrWhiteSpace(member) ? "" : member!;
		string lineStr = (line ?? 0).ToString();
		bool haveCaller = !string.IsNullOrWhiteSpace(caller);
		string? typeName = _rlogConfig.IncludeTypeInPrefix ? (CategoryName ?? null) : null;
		if (typeName != null)
		{
			if (_rlogConfig.IncludeCallerInPrefix && haveCaller)
			{
				consoleMessage = $"{typeName}.{caller}:{lineStr} - {message}";
			}
			else
			{
				consoleMessage = $"{typeName}:{lineStr} - {message}";
			}
		}
		else if (_rlogConfig.IncludeCallerInPrefix && haveCaller)
		{
			consoleMessage = $"{caller}:{lineStr} - {message}";
		}

		// Print if log level is enabled and ConsolePrint is true for this level
		if (ShouldPrintToConsole(level))
		{
			try
			{
				WriteColorizedConsoleLog(level, consoleMessage);
			}
			catch
			{
				// Silently ignore console write failures to prevent logging from crashing the application
			}
		}

		// Create the Record first to get its Id
		Rlog rlog = new(
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
				_eventPublisher.PublishLogEvent(new RlogEvent
				{
					Id = rlog.Id,
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

		return rlog;
	}

	/// <summary>
	/// Determines whether the specified log level should print to console based on configuration
	/// </summary>
	/// <param name="level">The log level to check</param>
	/// <returns>True if the level should print to console, false otherwise</returns>
	private bool ShouldPrintToConsole(LogLevel level)
	{
		return level switch
		{
			LogLevel.Debug => _rlogConfig.Debug.ConsolePrint,
			LogLevel.Info => _rlogConfig.Info.ConsolePrint,
			LogLevel.Warning => _rlogConfig.Warning.ConsolePrint,
			LogLevel.Error => _rlogConfig.Error.ConsolePrint,
			LogLevel.Fatal => _rlogConfig.Fatal.ConsolePrint,
			_ => true
		};
	}

	/// <summary>
	/// Writes a colorized log message to the console with appropriate prefix and colors based on log level.
	/// </summary>
	/// <param name="level">The log level</param>
	/// <param name="message">The message to write</param>
	private void WriteColorizedConsoleLog(LogLevel level, string message)
	{
		string prefix;
		ConsoleColor prefixColor;
		ConsoleColor textColor;

		// Define prefixes and colors based on log level and configuration
		switch (level)
		{
			case LogLevel.Debug:
				prefix = "[DEBUG]";
				prefixColor = ParseConsoleColor(_rlogConfig.Debug.PrefixColor);
				textColor = ParseConsoleColor(_rlogConfig.Debug.TextColor);
				break;
			case LogLevel.Info:
				prefix = "[INFO]";
				prefixColor = ParseConsoleColor(_rlogConfig.Info.PrefixColor);
				textColor = ParseConsoleColor(_rlogConfig.Info.TextColor);
				break;
			case LogLevel.Warning:
				prefix = "[WARNING]";
				prefixColor = ParseConsoleColor(_rlogConfig.Warning.PrefixColor);
				textColor = ParseConsoleColor(_rlogConfig.Warning.TextColor);
				break;
			case LogLevel.Error:
				prefix = "[ERROR]";
				prefixColor = ParseConsoleColor(_rlogConfig.Error.PrefixColor);
				textColor = ParseConsoleColor(_rlogConfig.Error.TextColor);
				break;
			case LogLevel.Fatal:
				prefix = "[FATAL]";
				prefixColor = ParseConsoleColor(_rlogConfig.Fatal.PrefixColor);
				textColor = ParseConsoleColor(_rlogConfig.Fatal.TextColor);
				break;
			default:
				prefix = "[UNKNOWN]";
				prefixColor = ConsoleColor.Gray;
				textColor = ConsoleColor.Gray;
				break;
		}

		// Ensure console writes are atomic across threads
		lock (LogLock)
		{
			// Store original color
			var originalColor = Console.ForegroundColor;
			try
			{
				// Write prefix with its color
				Console.ForegroundColor = prefixColor;
				Console.Write(prefix);
				Console.Write(" ");

				// Write message with its color
				Console.ForegroundColor = textColor;
				Console.WriteLine(message);
			}
			finally
			{
				// Restore original color
				Console.ForegroundColor = originalColor;
			}
		}
	}

	/// <summary>
	/// Parses a color string to ConsoleColor enum value
	/// </summary>
	/// <param name="colorString">The color string to parse</param>
	/// <returns>The corresponding ConsoleColor value, or Gray if parsing fails</returns>
	private static ConsoleColor ParseConsoleColor(string colorString)
	{
		if (string.IsNullOrWhiteSpace(colorString))
			return ConsoleColor.Gray;

		if (Enum.TryParse<ConsoleColor>(colorString, true, out var color))
			return color;

		return ConsoleColor.Gray;
	}
	
	public async Task DumpLog(StringBuilder sb, string fileNamePrefix)
	{
		await DumpLog(sb.ToString(), fileNamePrefix);
	}

	/// <summary>
	/// Writes the specified text into a file with a given prefix. The file is saved in the user's home directory
	/// inside a 'ResenLogs' folder, formatted as 'prefix-yyyy-MMM-dd-h:mm:sstt.txt'. Optionally includes a log record.
	/// </summary>
	/// <param name="textToDump">The text to be saved into the file. If null or empty, this method performs no action.</param>
	/// <param name="fileNamePrefix">The prefix to use in the generated file name.</param>
	/// <param name="record">Optional log record associated with the text being dumped.</param>
	/// <returns>A task representing the asynchronous operation of writing the file.</returns>
	public async Task DumpLog(string? textToDump, string fileNamePrefix, Rlog? record = null)
	{
		if (string.IsNullOrEmpty(textToDump))
			return;

		await DumpLogSemaphore.WaitAsync();

		try
		{
			// Capture stack trace
			EnhancedStackTrace stackTrace = EnhancedStackTrace.Current();
			string stackTraceString = $"Stack Trace:\n{stackTrace}";

			// Prepend stack trace to the text
			textToDump = stackTraceString + "\n\n" + textToDump;
			
			// Publish log event with "dump" tag containing the entire string message
			if (_eventPublisher != null)
			{
				try
				{
					RlogEvent rlogEvent;
					
					if (record != null)
					{
						string tags = $"dump session_{SessionTime:yyyy-MMM-dd_HH-mm-ss} ";
						tags += record.Tags?.ToString();
						
						rlogEvent = new RlogEvent
						{
							Id = record.Id,
							ParentId = record.Parent?.Id,
							Timestamp = record.Timestamp,
							Level = record.Level,
							Message = textToDump,
							Identifier = record.Identifier,
							Cycle = record.Cycle,
							Tags = tags,
							Object1 = record.Object1 != null ? JsonConvert.SerializeObject(record.Object1, Formatting.Indented, new StringEnumConverter()) : null,
							Object2 = record.Object2 != null ? JsonConvert.SerializeObject(record.Object2, Formatting.Indented, new StringEnumConverter()) : null,
							File = record.File,
							Member = record.Member,
							Line = record.Line
						};
					}
					else
					{
						rlogEvent = new RlogEvent
						{
							Id = Guid.NewGuid().ToString(),
							Level = LogLevel.Info,
							Message = textToDump,
							Tags = $"dump session_{SessionTime:yyyy-MMM-dd_HH-mm-ss}",
							Timestamp = DateTime.UtcNow
						};
					}

					await _eventPublisher.PublishLogEventAsync(rlogEvent);
				}
				catch
				{
					// Silently ignore event publishing failures to prevent logging from crashing the application
				}
			}
			
			string homeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string fileSuffix = "_1";
			int counter = 2;
			
			string fullFilePath;
			string folder = $"session_{SessionTime:yyyy-MMM-dd_HH-mm-ss}";
			
			do
			{
				// Formatting the filename
				string fileName = $"{fileNamePrefix}{fileSuffix}.txt";
				
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