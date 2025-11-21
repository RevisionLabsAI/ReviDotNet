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
using System.Collections.Concurrent;
using System.IO;
using Microsoft.DeepDev;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Revi.Configuration;

namespace Revi;

public class ReviLogger : IReviLogger
{
	private static readonly object LimiterSync = new object();
	private static volatile bool LimiterInitialized = false;
	private static ConcurrentDictionary<string, LogLevel> _consoleLimiter = new ConcurrentDictionary<string, LogLevel>(StringComparer.Ordinal);
	// New: exact call-site suppression entries, keyed as "Class.Method:Line"
	private static ConcurrentDictionary<string, bool> _consoleLineSuppress = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
	private static FileSystemWatcher? _limiterWatcher;
	private static string? _limiterPath;
	private static readonly object LogLock = new object();
	private static readonly SemaphoreSlim DumpLogSemaphore = new SemaphoreSlim(1, 1);
	private static readonly DateTime SessionTime = DateTime.Now;
	
	// Process-wide identity so all logger instances in this process share the same InstanceId
	private static readonly string ProcessInstanceId;
	private static readonly DateTimeOffset ProcessStartUtc;

	// Static constructor runs once per process to establish process identity
	static ReviLogger()
	{
		ProcessInstanceId = NodeIdentity.InstanceIdUtc(out ProcessStartUtc);
	}
	
	 private readonly IRlogEventPublisher? _eventPublisher;
	private readonly RlogConfiguration _rlogConfig;
	private readonly string _machineId;
	private readonly string _instanceId;
	private readonly DateTimeOffset _instanceStartUtc;
	
	// Overridable category/type name used for prefix formatting; generic logger overrides this
	protected virtual string? CategoryName => null;
	
	public ReviLogger(
		IRlogEventPublisher eventPublisher,
		IConfiguration configuration)
	{
		_eventPublisher = eventPublisher;
		_rlogConfig = configuration.GetSection("ReviLogger").Get<RlogConfiguration>() ?? GetDefaultRlogConfiguration();
		// Initialize identity
		// Use process-wide identity so every ReviLogger inside this process shares one InstanceId
		_instanceId = ProcessInstanceId;
		_instanceStartUtc = ProcessStartUtc;
		_machineId = NodeIdentity.GetMachineId(appName: TryGetAppName(), machineWide: true);

		// Initialize limiter (one-time, shared across instances)
		EnsureLimiterInitialized();
	}

	/// <summary>
	/// Gets the default Rlog configuration
	/// </summary>
 private static RlogConfiguration GetDefaultRlogConfiguration()
	{
		// Enable stack-based legacy type resolution by default in Development environments
		string? env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
			?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
		bool isDevelopment = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(env, "Dev", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(env, "Local", StringComparison.OrdinalIgnoreCase);

		return new RlogConfiguration
		{
			Debug = new RlogLevelConfiguration { PrefixColor = "Green", TextColor = "Gray", ConsolePrint = true },
			Info = new RlogLevelConfiguration { PrefixColor = "Blue", TextColor = "White", ConsolePrint = true },
			Warning = new RlogLevelConfiguration { PrefixColor = "Yellow", TextColor = "White", ConsolePrint = true },
			Error = new RlogLevelConfiguration { PrefixColor = "DarkYellow", TextColor = "DarkYellow", ConsolePrint = true },
			Fatal = new RlogLevelConfiguration { PrefixColor = "Red", TextColor = "Red", ConsolePrint = true },
			ResolveLegacyTypeFromStack = isDevelopment
		};
	}

	public Rlog LogInfo(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name, 
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name, 
			object2,
			object2Name, 
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		[CallerArgumentExpression(nameof(object1))] string? object1Name = null,
		object? object2 = null,
		[CallerArgumentExpression(nameof(object2))] string? object2Name = null,
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
			object1Name,
			object2,
			object2Name,
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
		string? object1Name = null,
		object? object2 = null,
		string? object2Name = null,
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
		string caller = string.IsNullOrWhiteSpace(member) ? "" : NormalizeMember(member)!;
		string lineStr = (line ?? 0).ToString();
		bool haveCaller = !string.IsNullOrWhiteSpace(caller);
		string? typeName = _rlogConfig.IncludeTypeInPrefix ? (CategoryName ?? null) : null;

		// Determine effective level for legacy Util.Log based on message keywords
		LogLevel effectiveLevel = level;
		bool isLegacyUtil = !string.IsNullOrWhiteSpace(tags) && tags.Contains("legacyutil", StringComparison.OrdinalIgnoreCase);
		if (isLegacyUtil)
		{
			if (TryInferLegacyLevelFromMessage(message, out var inferred))
			{
				effectiveLevel = inferred;
			}
		}
		
		// Special-case: legacy Util.Log calls: optionally resolve originating class via stack when enabled
		if (typeName == null && _rlogConfig.IncludeTypeInPrefix)
		{
			if (isLegacyUtil)
			{
				// Prefer stack-based discovery when configured; fall back to generic UtilLog
				if (_rlogConfig.ResolveLegacyTypeFromStack)
				{
					try
					{
						string? resolved = TryResolveLegacyCallerTypeFromStack();
						if (!string.IsNullOrWhiteSpace(resolved))
							typeName = resolved;
					}
					catch { /* ignore stack resolution failures */ }
				}
				if (typeName == null)
					typeName = "UtilLog";
			}
		}
		
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
		string? classForLimiter = CategoryName;
		if (classForLimiter == null && _rlogConfig.IncludeTypeInPrefix)
		{
			// when IncludeTypeInPrefix is enabled, typeName may have been resolved above (e.g., legacy)
			classForLimiter = typeName;
		}
		if (ShouldPrintToConsole(effectiveLevel, classForLimiter, caller, line))
		{
			try
			{
				WriteColorizedConsoleLog(effectiveLevel, consoleMessage);
			}
			catch
			{
				// Silently ignore console write failures to prevent logging from crashing the application
			}
		}

		// Create the Record first to get its Id
		Rlog rlog = new(
			parent, 
			effectiveLevel, 
			message, 
			identifier, 
			cycle, 
			tags, 
			object1,
			object1Name, 
			object2,
			object2Name, 
			file ?? "", 
			NormalizeMember(member) ?? "", 
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
        Level = effectiveLevel,
        Message = message,
        Identifier = identifier,
        Cycle = cycle,
        Tags = tags,
        Object1 = object1 != null ? JsonConvert.SerializeObject(object1, Formatting.Indented, new StringEnumConverter()) : null,
        Object1Name = object1Name,
        Object2 = object2 != null ? JsonConvert.SerializeObject(object2, Formatting.Indented, new StringEnumConverter()) : null,
        Object2Name = object2Name,
        File = file,
        Member = NormalizeMember(member),
        // Prefer resolved typeName when available, fallback to CategoryName if set
        ClassName = (CategoryName ?? (_rlogConfig.IncludeTypeInPrefix ? (typeName ?? null) : null)),
        Line = line,
        MachineId = _machineId,
        InstanceId = _instanceId
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
	private bool ShouldPrintToConsole(LogLevel level, string? className, string? methodName, int? lineNumber)
	{
		// First, honor limiter overrides
		// 1) Exact Class.Method match (case-sensitive)
		// 2) Fallback to Method-only match when class is unavailable or no class-level entry exists
		// 3) Exact Class.Method:Line suppression (if present, always suppress)
		try
		{
			bool haveClass = !string.IsNullOrWhiteSpace(className);
			bool haveMethod = !string.IsNullOrWhiteSpace(methodName);
			if (haveClass && haveMethod && lineNumber.HasValue)
			{
				string siteKey = className + "." + methodName + ":" + lineNumber.Value.ToString();
				if (_consoleLineSuppress.ContainsKey(siteKey))
				{
					return false; // exact call-site suppression
				}
			}
			if (haveMethod)
			{
				if (haveClass)
				{
					string key = className + "." + methodName; // case-sensitive as per requirement
					if (_consoleLimiter.TryGetValue(key, out LogLevel minLevel))
					{
						if (level < minLevel)
							return false; // do not print, but event still created by caller
						return true; // specific override matched and allows printing
					}
				}

				// Method-only fallback: ONLY when class name is unavailable (non-typed logger or no resolved class)
				if (!haveClass && _consoleLimiter.TryGetValue(methodName!, out LogLevel methodMin))
				{
					if (level < methodMin)
						return false;
					return true;
				}
			}
		}
		catch { /* never fail logging due to limiter issues */ }

		// Fall back to global console print flags
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

	private static void EnsureLimiterInitialized()
	{
		if (LimiterInitialized) return;
		lock (LimiterSync)
		{
			if (LimiterInitialized) return;
			try
			{
				_limiterPath = ResolveLimiterPath();
				LoadLimiterFile(_limiterPath);
				SetupWatcher(_limiterPath);
			}
			catch
			{
				// Never throw from logger init
			}
			finally
			{
				LimiterInitialized = true;
			}
		}
	}

	private static string? ResolveLimiterPath()
	{
		// Priority: ENV var, then app base file (txt), then solution BetterNamer.Blazor root (txt),
		// then legacy locations (.rcfg in RConfigs)
		try
		{
			string? envPath = Environment.GetEnvironmentVariable("REVILOGGER_LIMITER_PATH");
			if (!string.IsNullOrWhiteSpace(envPath))
				return envPath;

			string baseDir = AppContext.BaseDirectory;
			// New preferred filename in app base directory
			string newTxt = Path.Combine(baseDir, "revilogger_limiter.txt");
			if (File.Exists(newTxt)) return newTxt;

			// When running from source, check solution root + BetterNamer.Blazor root for the new txt
			string? solutionRoot = TryFindSolutionRoot(baseDir);
			if (solutionRoot != null)
			{
				string blazorTxt = Path.Combine(solutionRoot, "BetterNamer.Blazor", "revilogger_limiter.txt");
				if (File.Exists(blazorTxt)) return blazorTxt;
			}

			// Legacy fallbacks: RConfigs\revilogger_limiter.rcfg in app base and repo
			string rconfigs = Path.Combine(baseDir, "RConfigs");
			string candidate = Path.Combine(rconfigs, "revilogger_limiter.rcfg");
			if (File.Exists(candidate)) return candidate;

			if (solutionRoot != null)
			{
				string blazorPath = Path.Combine(solutionRoot, "BetterNamer.Blazor", "RConfigs", "revilogger_limiter.rcfg");
				if (File.Exists(blazorPath)) return blazorPath;
			}
		}
		catch { }
		return null;
	}

	private static string? TryFindSolutionRoot(string startDir)
	{
		try
		{
			string? dir = startDir;
			for (int i = 0; i < 6 && dir != null; i++)
			{
				string sln = Path.Combine(dir, "BetterNamer.sln");
				if (File.Exists(sln)) return dir;
				dir = Path.GetDirectoryName(dir);
			}
		}
		catch { }
		return null;
	}

 private static void LoadLimiterFile(string? path)
	{
		try
		{
			var dict = new ConcurrentDictionary<string, LogLevel>(StringComparer.Ordinal);
			var siteSuppress = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			{
				foreach (var rawLine in File.ReadAllLines(path))
				{
					string line = rawLine.Trim();
					if (line.Length == 0) continue;
					if (line.StartsWith("#") || line.StartsWith("//")) continue;
					int eq = line.IndexOf('=');
					if (eq > 0 && eq < line.Length - 1)
					{
						string key = line.Substring(0, eq).Trim(); // case-sensitive key
						string val = line.Substring(eq + 1).Trim();
						if (string.IsNullOrEmpty(key)) continue;
						if (Enum.TryParse<LogLevel>(val, ignoreCase: true, out var level))
						{
							dict[key] = level;
						}
					}
					else
					{
						// Support bare entries of the form Class.Method:Line for exact suppression
						// Validate simple structure to avoid accidental catches
						if (line.Contains(':') && line.Contains('.'))
						{
							siteSuppress[line] = true;
						}
					}
				}
			}
			_consoleLimiter = dict; // swap atomically
			_consoleLineSuppress = siteSuppress; // swap atomically
		}
		catch
		{
			// ignore
		}
	}

	private static void SetupWatcher(string? path)
	{
		try
		{
			_limiterWatcher?.Dispose();
			_limiterWatcher = null;
			if (string.IsNullOrWhiteSpace(path)) return;
			string? dir = Path.GetDirectoryName(path);
			string? file = Path.GetFileName(path);
			if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file)) return;
			var watcher = new FileSystemWatcher(dir, file)
			{
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
			};
			watcher.Changed += (_, __) => LoadLimiterFile(path);
			watcher.Created += (_, __) => LoadLimiterFile(path);
			watcher.Renamed += (_, __) => LoadLimiterFile(path);
			watcher.EnableRaisingEvents = true;
			_limiterWatcher = watcher;
		}
		catch { }
	}

	private static bool TryInferLegacyLevelFromMessage(string? message, out LogLevel level)
	{
		level = LogLevel.Info;
		try
		{
			if (string.IsNullOrWhiteSpace(message)) return false;
			string text = message!;
			string lower = text.ToLowerInvariant();

			// Map of keywords to levels; choose earliest occurrence across all
			(string[] keys, LogLevel lvl)[] sets = new (string[], LogLevel)[]
			{
				(new[]{"fatal","critical","crit","severe"}, LogLevel.Fatal),
				(new[]{"error","err","exception","failed","fail","failure"}, LogLevel.Error),
				(new[]{"warn","warning"}, LogLevel.Warning),
				(new[]{"info","information"}, LogLevel.Info),
				(new[]{"debug","trace"}, LogLevel.Debug)
			};

			int bestIndex = int.MaxValue;
			LogLevel bestLevel = level;
			foreach (var set in sets)
			{
				foreach (var k in set.keys)
				{
					int idx = lower.IndexOf(k, StringComparison.Ordinal);
					if (idx >= 0 && idx < bestIndex)
					{
						bestIndex = idx;
						bestLevel = set.lvl;
					}
				}
			}

			if (bestIndex != int.MaxValue)
			{
				level = bestLevel;
				return true;
			}
		}
		catch { }
		return false;
	}

	/// <summary>
	/// Writes a colorized log message to the console with appropriate prefix and colors based on log level.
	/// </summary>
	/// <param name="level">The log level</param>
	/// <param name="message">The message to write</param>
	private static string TryGetAppName()
	{
		try
		{
			var asm = Assembly.GetEntryAssembly() ?? typeof(ReviLogger).Assembly;
			return asm.GetName().Name ?? "ReviDotNet";
		}
		catch { return "ReviDotNet"; }
	}

	// Normalizes member names for readability in logs.
	// - ".ctor"  -> "Constructor"
	// - "<Main>$" -> "Main"
	private static string? NormalizeMember(string? member)
	{
		if (string.IsNullOrWhiteSpace(member)) return member;
		if (string.Equals(member, ".ctor", StringComparison.Ordinal)) return "Constructor";
		if (string.Equals(member, "<Main>$", StringComparison.Ordinal)) return "Main";
		return member;
	}

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
				prefix = "[WARN]";
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

	/// <summary>
	/// Attempts to resolve the originating caller type for legacy Util.Log calls by inspecting the current stack trace.
	/// Skips frames belonging to logging infrastructure and platform namespaces.
	/// </summary>
	/// <returns>Type name if found; otherwise null.</returns>
	private static string? TryResolveLegacyCallerTypeFromStack()
	{
		try
		{
			var st = new StackTrace();
			for (int i = 0; i < st.FrameCount; i++)
			{
				var frame = st.GetFrame(i);
				var method = frame?.GetMethod();
				var type = method?.DeclaringType;
				if (type == null)
					continue;

				string ns = type.Namespace ?? string.Empty;
				string tn = type.Name ?? string.Empty;

				// Exclude obvious infrastructure namespaces/types
				if (ns.StartsWith("System", StringComparison.Ordinal)
					|| ns.StartsWith("Microsoft", StringComparison.Ordinal)
					|| ns.StartsWith("Newtonsoft", StringComparison.Ordinal)
					|| ns.StartsWith("Serilog", StringComparison.Ordinal))
					continue;

				// Exclude our own logging/utilities
				if (ns.StartsWith("Revi", StringComparison.Ordinal))
				{
					if (tn.Contains("ReviLogger", StringComparison.Ordinal)
						|| tn.Equals("Util", StringComparison.Ordinal)
						|| tn.Contains("ReviServiceLocator", StringComparison.Ordinal))
						continue;
				}

				// Exclude compiler generated artifacts
				if (tn.StartsWith("<", StringComparison.Ordinal))
					continue;

				// First remaining candidate is our caller
				return tn;
			}
		}
		catch
		{
			// ignore and return null
		}
		return null;
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