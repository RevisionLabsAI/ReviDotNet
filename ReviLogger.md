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

ReviLogger lives in the ReviDotNet.Core package. **The canonical way to register it is `AddReviDotNet(...)`**, which wires `IReviLogger`/`IReviLogger<T>` (via `TryAddSingleton`) along with the rest of the library. Don't register `ReviLogger` by hand unless you know you need to — its constructor **requires a non-null `IRlogEventPublisher`**, which `AddReviDotNet` does not register, so a bare `AddSingleton<IReviLogger, ReviLogger>()` fails to resolve unless you also register a publisher.

Two startup steps matter:

1. **Register an `IRlogEventPublisher`.** If you don't have a real sink, register a no-op so the logger can construct (and optionally plug in a Mongo/live-viewer publisher later).
2. **Call `ReviServiceLocator.SetProvider(app.Services)` after building the host.** Static `Util.Log` and `AgentReviLogger` resolve their logger through `ReviServiceLocator.TryGetLogger`; without `SetProvider`, those calls can't find the logger and agent/log correlation is silently lost.

Example for ASP.NET Core / generic host:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Revi;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Register the whole library, including IReviLogger / IReviLogger<T>.
builder.Services.AddReviDotNet(typeof(Program).Assembly);

// Provide an event publisher (a no-op is fine if you have no external sink yet).
builder.Services.AddSingleton<IRlogEventPublisher, NoOpRlogEventPublisher>();

IHost app = builder.Build();

// Required so Util.Log / AgentReviLogger can resolve the logger via the service locator.
ReviServiceLocator.SetProvider(app.Services);

IReviLogger logger = app.Services.GetRequiredService<IReviLogger>();
logger.LogInfo("ReviLogger is ready");

// Typed logger (category = T's name) is also available:
IReviLogger<MyService> typed = app.Services.GetRequiredService<IReviLogger<MyService>>();

await app.RunAsync();
```

Inject `IReviLogger` (or `IReviLogger<T>` for an automatic category) into your components/services/controllers as usual.

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

Color names map to System.ConsoleColor (e.g., Gray, DarkGray, Blue, Cyan, Yellow, Red, DarkYellow, Green). If a color is missing or invalid, ReviLogger falls back to **Gray** (for both prefix and text — never White). Parsing is case-insensitive.

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

Each method has the full signature `(... object1?, object1Name?, object2?, object2Name?, file?, member?, line?)`. You normally only pass `message` and optionally `object1`/`object2`:

- `object1Name`/`object2Name` are `[CallerArgumentExpression]` parameters — they **auto-capture the source text of the expression** you passed as `object1`/`object2`. So `LogDebug("Config", object1: cfg)` records the object under the name `"cfg"` automatically; you rarely set these explicitly.
- `file`/`member`/`line` are `[CallerFilePath]`/`[CallerMemberName]`/`[CallerLineNumber]` — caller info is auto-captured; you don't pass them.

Note: parent is an Rlog returned by a previous call; pass it to correlate child log entries.

### `IsEnabled(LogLevel)`

`IReviLogger` also declares `bool IsEnabled(LogLevel level)`. It returns whether that level's **console printing** is on (the level's `ConsolePrint` flag — see configuration), **not** a min-level threshold. Use it to guard expensive message construction:

```csharp
if (_log.IsEnabled(LogLevel.Debug))
    _log.LogDebug(BuildExpensiveDiagnostic());
```

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

> **Normalization — match the stored form when filtering downstream.** Identifiers and tags are normalized on the `Rlog` record, so a sink/query must match the normalized form, not what you typed:
> - **Identifier** is lower-cased and spaces become hyphens: `"Begin Loop"` → `"begin-loop"`. (If you pass no identifier, it defaults to the caller member name, or the level name.)
> - **Tags** are split on **space *or* comma**, empty entries dropped, and each tag is lower-cased and trimmed: `"Parse, IO"` and `"parse io"` both yield `["parse", "io"]`. Tag matching is therefore effectively case-insensitive.

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

> **Memory note:** passing a `parent` does more than set a correlation id. Each child's message is `AppendLine`-d into the parent's `StringBuilder` **and every ancestor's** builder, so a root `Rlog` accumulates the full text of its entire subtree (this is what enables a later `DumpLog(root)` of the whole tree). For short-lived scopes this is fine, but a **long-lived root** (e.g. one held for the lifetime of the process and chained under continuously) grows that text in memory without bound. Keep parent chains scoped to a unit of work, or dump-and-drop the root periodically.

---

## 5) Attaching contextual objects

You can attach up to two arbitrary objects per log call via object1 and object2. When the Rlog event is published, each attached object is serialized as follows:
- **Serializer:** Newtonsoft.Json (`JsonConvert.SerializeObject`) with `Formatting.Indented` and a `StringEnumConverter`, so **enums are emitted as their string names**, not numbers. (System.Text.Json is *not* used in the logging path.) Because Newtonsoft is used, beware of cyclic object graphs.
- **Secret redaction:** the serialized text is passed through `Util.RedactSecrets` before it reaches any sink, so API keys in URLs / `Authorization` headers are scrubbed.
- Exceptions can be attached to capture message/stack details.

Note: object payloads are **published only** (to the `IRlogEventPublisher` sink / live viewer) — they are **not** written to the colorized console output. Only the message line is printed to the console.

Tip: Favor small, relevant payloads to keep logs readable; large objects are better dumped using DumpLog below.

---

## 6) Dumping text and images

When you need to persist larger artifacts, use the dump helpers. They write to a **fixed, non-configurable** location: `<UserProfile>/ResenLogs/session_<yyyy-MMM-dd_HH-mm-ss>/<prefix>_<n>.<ext>`. The timestamp is the **folder** name (per process session); the file name is just your `fileNamePrefix` plus a numeric counter (`_1`, `_2`, …) and the extension (`.txt` for text, the image extension for images). There is no setting to change the directory.

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

ReviLogger colorizes console output per level. A console line is exactly two parts: the **level tag** then the **message line**. You can control:
- **PrefixColor**: color for the leading level tag **only** — i.e. `[DEBUG]` / `[INFO]` / `[WARN]` / `[ERROR]` / `[FATAL]`. **No timestamp is printed** to the console, and the type/caller is *not* part of this prefix.
- **TextColor**: color for the **message line**, which is everything after the tag. When `IncludeTypeInPrefix` / `IncludeCallerInPrefix` are enabled, the type and `caller:line` are formatted *into the message line* (e.g. `MyService.DoWork:42 - <message>`) and therefore take **TextColor**, not PrefixColor. Object payloads are not rendered to the console at all (see §5).
- **ConsolePrint**: whether a given level should be printed to console

Colorization can be disabled per level by setting ConsolePrint to false, or globally by setting all levels to ConsolePrint=false.

### Console limiter file (per-site verbosity, hot-reloaded)

Beyond the global per-level flags, ReviLogger reads a **limiter file** that lets you tune console verbosity per call-site **without redeploying** — it is watched with a `FileSystemWatcher` and reloaded live on change. It affects **console output only**; events are still published to any `IRlogEventPublisher` sink regardless.

**File location** (first match wins):
1. The path in the `REVILOGGER_LIMITER_PATH` environment variable.
2. `revilogger_limiter.txt` in the app base directory.
3. Legacy fallbacks (`revilogger_limiter.rcfg` under `RConfigs/`).

**Entry formats** (one per line):
- `Class.Method = Level` — sets the **minimum** console level for that call site (messages below it aren't printed). The `Class.Method` key is **case-sensitive**; the level (`Debug`/`Info`/`Warning`/`Error`/`Fatal`) is case-insensitive.
- `Class.Method:Line` — a bare entry (no `=`) **suppresses** that exact call site (by line).

Lines starting with `#` or `//` are comments. Example:

```
# quiet a chatty importer, but keep its errors
DataImporter.Run = Error
# silence one specific noisy call site
DataImporter.Poll:128
```

---

## 8) Environment-based defaults

ReviLogger detects environment via DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT. In Development/Dev/Local it enables ResolveLegacyTypeFromStack, which attempts to infer legacy caller type names from the stack for nicer prefixes. In other environments this is disabled by default for performance reasons.

### Legacy `Util.Log` and the `legacyutil` tag (level is inferred from the message)

The static `Util.Log(...)` shim is meant for migrating old call sites. It always stamps the tag `legacyutil <file>:<member>:<line>` onto the event. The `legacyutil` tag triggers **two special behaviors** in the logger:

1. **Level is inferred from the message text — not from the call.** When a log's tags contain `legacyutil` (case-insensitive), ReviLogger scans the **message** for the *earliest-occurring* keyword and overrides the level accordingly:

   | Inferred level | Keywords (case-insensitive, substring match) |
   |---|---|
   | Fatal | `fatal`, `critical`, `crit`, `severe` |
   | Error | `error`, `err`, `exception`, `failed`, `fail`, `failure` |
   | Warning | `warn`, `warning` |
   | Info | `info`, `information` |
   | Debug | `debug`, `trace` |

   The keyword nearest the **start** of the message wins; if none match, the level is left as passed. This means a call like `Util.Log("Connection failed, retrying...")` silently becomes an **Error**-level event — convenient for legacy logs, surprising if you don't expect it. (Note the substring match: a message mentioning "no warnings found" still matches `warn`.) Prefer the typed `IReviLogger.LogXxx` methods, which set the level explicitly and are not subject to this inference.

2. **Type resolution falls back to the stack.** When `IncludeTypeInPrefix` is on but no `CategoryName` is set, a `legacyutil` event resolves its originating class from the stack trace (when `ResolveLegacyTypeFromStack` is enabled, see above), otherwise it labels the type `UtilLog`.

To opt a non-`Util.Log` call into this behavior, include `legacyutil` in its `tags`; to keep an explicit level, leave the tag off and use the typed methods.

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