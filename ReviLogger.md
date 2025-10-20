# ReviLogger Guide

A quick, practical guide to using ReviLogger for structured, colorized logging and artifact dumping in .NET apps.

ReviLogger provides:
- Simple log methods for common levels: Debug, Info, Warning, Error, Fatal
- Rich optional context: identifier, cycle, tags, two object payloads
- Parent/child correlation via Rlog records
- Colorized console output with per-level configuration
- Dump helpers for text/StringBuilder and binary images

This guide covers configuration, DI setup, usage patterns, and tips.

---

## 1) Installation and DI registration

ReviLogger lives in the ReviDotNet.Core package within this repository. Register it in your DI container and inject IReviLogger where needed.

Example for ASP.NET Core / generic host:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Revi;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Add configuration (appsettings.json etc.)
// builder.Configuration.AddJsonFile("appsettings.json", optional: false);

// Register ReviLogger and its dependencies
builder.Services.AddSingleton<IReviLogger, ReviLogger>();

// Optional: if you have an IRlogEventPublisher implementation
// builder.Services.AddSingleton<IRlogEventPublisher, YourEventPublisher>();

IHost app = builder.Build();

IReviLogger logger = app.Services.GetRequiredService<IReviLogger>();
logger.LogInfo("ReviLogger is ready");

await app.RunAsync();
```

In minimal APIs or Blazor Server you can register in Program.cs similarly, then inject IReviLogger into your components/services/controllers.

---

## 2) Configuration (appsettings)

ReviLogger reads configuration from the Configuration section "ReviLogger" and falls back to sensible defaults if not present.

Defaults (when not configured):
- ConsolePrint: true for all levels
- PrefixColor/TextColor by level: Debug=Green/Gray, Info=Blue/White, Warning=Yellow/White, Error=DarkYellow/DarkYellow, Fatal=Red/Red
- ResolveLegacyTypeFromStack: true when DOTNET_ENVIRONMENT/ASPNETCORE_ENVIRONMENT is Development/Dev/Local; otherwise false

Example appsettings.json:

```json
{
  "ReviLogger": {
    "Debug": { "PrefixColor": "Green", "TextColor": "DarkGray", "ConsolePrint": true },
    "Info": { "PrefixColor": "Cyan",  "TextColor": "White",    "ConsolePrint": true },
    "Warning": { "PrefixColor": "Yellow", "TextColor": "White", "ConsolePrint": true },
    "Error": { "PrefixColor": "Red", "TextColor": "Red", "ConsolePrint": true },
    "Fatal": { "PrefixColor": "Red", "TextColor": "Red", "ConsolePrint": true },
    "ResolveLegacyTypeFromStack": false
  }
}
```

Color names map to System.ConsoleColor (e.g., Gray, DarkGray, Blue, Cyan, Yellow, Red, DarkYellow, Green). If a color is invalid, ReviLogger will default to Gray/White.

---

## 3) Basic usage

IReviLogger exposes methods per level and a general Log method. All logging methods return an Rlog record you can pass as a parent to correlate related logs.

### Methods
- LogDebug(message, identifier?, cycle?, tags?, object1?, object2?)
- LogDebug(parent, message, identifier?, cycle?, tags?, object1?, object2?)
- LogInfo(message, identifier?, cycle?, tags?, object1?, object2?)
- LogInfo(parent, message, identifier?, cycle?, tags?, object1?, object2?)
- LogWarning(message, identifier?, cycle?, tags?, object1?, object2?)
- LogWarning(parent, message, identifier?, cycle?, tags?, object1?, object2?)
- LogError(message, identifier?, cycle?, tags?, object1?, object2?)
- LogError(parent, message, identifier?, cycle?, tags?, object1?, object2?)
- LogFatal(message, identifier?, cycle?, tags?, object1?, object2?)
- LogFatal(parent, message, identifier?, cycle?, tags?, object1?, object2?)
- Log(parent?, level, message, identifier?, cycle?, tags?, object1?, object2?)

Note: parent is an Rlog returned by a previous call; pass it to correlate child log entries.

Caller info (file, member, line) is auto-captured via Caller* attributes; you normally don’t pass those.

### Examples

```csharp
public class MyService
{
    private readonly IReviLogger _log;
    public MyService(IReviLogger log) => _log = log;

    private sealed class WorkConfig
    {
        public int Size { get; init; }
        public string Mode { get; init; } = string.Empty;
    }

    public void DoWork()
    {
        _log.LogInfo("Starting work", identifier: "job-42", tags: "startup");

        WorkConfig cfg = new() { Size = 10, Mode = "fast" };
        _log.LogDebug("Configuration", object1: cfg);

        try
        {
            // ... work
        }
        catch (Exception ex)
        {
            _log.LogError($"Failed: {ex.Message}", tags: "exception", object1: ex);
            throw;
        }
    }
}
```

- identifier: any string to correlate across boundaries (request id, job id, etc.)
- cycle: an integer to represent attempts/retries/loops
- tags: free-form comma/space-separated labels for filtering
- object1/object2: any objects to serialize/print alongside the message

---

## 4) Parent/child correlation with Rlog

Each log method returns an Rlog record. Pass it as parent to subsequent logs to maintain a hierarchy and shared context.

```csharp
Rlog root = _log.LogInfo("Import started", identifier: "import-2025-10-19");

for (int i = 0; i < files.Count; i++)
{
    Rlog step = _log.LogDebug(root, $"Processing {files[i].Name}", cycle: i);
    try
    {
        // Step details
        _log.LogInfo(step, "Parsed header", tags: "parse");
        _log.LogInfo(step, "Saved record", tags: "persist");
    }
    catch (Exception ex)
    {
        _log.LogError(step, $"File failed: {ex.Message}", tags: "error", object1: ex);
    }
}
```

Parent/child usage keeps correlation ids, identifiers, and other metadata coherent across related operations.

---

## 5) Attaching contextual objects

You can attach up to two arbitrary objects per log call via object1 and object2. ReviLogger will serialize and render them:
- Complex objects via System.Text.Json (with safe options) and/or simple property printing
- Strings are printed as-is
- Exceptions can be attached to capture stack details

Tip: Favor small, relevant payloads to keep logs readable; large objects are better dumped using DumpLog below.

---

## 6) Dumping text and images

When you need to persist larger artifacts, use the dump helpers. These write files using a consistent naming scheme (prefix + timestamp + session/instance identifiers) to the default dump location resolved by ReviLogger.

### Dump large text or builders
```csharp
// StringBuilder overload
StringBuilder sb = new StringBuilder();
sb.AppendLine("Header");
sb.AppendLine(hugeText);
await _log.DumpLog(sb, fileNamePrefix: "import-summary");

// String overload with optional correlation record
await _log.DumpLog(hugeText, fileNamePrefix: "import-raw", record: root);
```

### Dump binary image bytes
```csharp
byte[] pngBytes = await File.ReadAllBytesAsync("chart.png");
await _log.DumpImage(pngBytes, fileNamePrefix: "metrics-chart", extension: "png");
```

Notes:
- extension should not include the dot (e.g., "png", "jpg")
- fileNamePrefix is a short, filesystem-safe label (e.g., "session1", "order-123")

---

## 7) Console output and colors

ReviLogger colorizes console output per level. You can control:
- PrefixColor: color for the leading structured prefix (level, timestamp, type)
- TextColor: color for the message line and payload
- ConsolePrint: whether a given level should be printed to console

Colorization can be disabled per level by setting ConsolePrint to false, or globally by setting all levels to ConsolePrint=false.

---

## 8) Environment-based defaults

ReviLogger detects environment via DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT. In Development/Dev/Local it enables ResolveLegacyTypeFromStack, which attempts to infer legacy caller type names from the stack for nicer prefixes. In other environments this is disabled by default for performance reasons.

---

## 9) Advanced notes

- CategoryName: ReviLogger has a protected virtual CategoryName used for prefix formatting. If you create a derived logger, you can override it to stamp a category/type name.
- Machine and Instance identity: Internally ReviLogger stamps machine id and instance id (startup time) using NodeIdentity, enabling cross-node correlation.
- Event publisher: If an IRlogEventPublisher is provided via DI, ReviLogger can publish structured Rlog events to external sinks or live viewers. Implement and register IRlogEventPublisher to integrate with your observability pipeline.

---

## 10) Best practices

- Choose identifiers that map to a business or technical transaction (request id, job id, user id)
- Use cycle to represent retries/loops; start at 0 and increment
- Add short tags for later filtering (e.g., parse, io, db, cache, retry)
- Attach small, informative objects (configs, inputs summary, outputs summary). Dump big artifacts via DumpLog/DumpImage
- Control console verbosity via appsettings per deployment target
- Keep fileNamePrefix short and filesystem-safe (no spaces or special characters)

---

## 11) Troubleshooting

- No console output: ensure ReviLogger section exists or defaults apply, and ConsolePrint is true for the level you’re using
- Colors look off: verify PrefixColor/TextColor are valid ConsoleColor names
- Caller info missing: do not wrap logger methods in your own methods without Caller* passthrough; prefer injecting IReviLogger directly
- Dump files not appearing: ensure the process has write permissions to the current working directory or the configured dump path (ReviLogger resolves a safe temp/data location depending on host)

---

## 12) API reference (summary)

```csharp
public interface IReviLogger
{
    Rlog LogInfo(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);
    Rlog LogInfo(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);

    Rlog LogDebug(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);
    Rlog LogDebug(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);

    Rlog LogWarning(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);
    Rlog LogWarning(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);

    Rlog LogError(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);
    Rlog LogError(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);

    Rlog LogFatal(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);
    Rlog LogFatal(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);

    Rlog Log(Rlog? parent, LogLevel level, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, object? object2 = null);

    Task DumpLog(StringBuilder sb, string fileNamePrefix);
    Task DumpLog(string? textToDump, string fileNamePrefix, Rlog? record = null);
    Task DumpImage(byte[] imageBytes, string fileNamePrefix, string extension = "png");
}
```

---

## 13) Changelog

- 2025-10-19: Initial guide added.