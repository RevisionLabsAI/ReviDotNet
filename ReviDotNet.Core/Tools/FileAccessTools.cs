// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Runtime.ExceptionServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// Built-in tools that let an agent work with files the user attached to the session. The agent is
/// never handed raw file bytes in its own context — it is told the files exist (via a manifest the
/// runner injects) and reads them on demand through these tools. <c>read-file</c> / <c>search-files</c>
/// delegate the heavy reading to a fresh reader LLM (vision-capable for images) so a large document
/// or an image is summarised against a focused query in a separate context window, keeping the main
/// agent's context clean. Files are reached via <see cref="AgentRunContext.Current"/>.<see cref="AgentRunContext.Files"/>.
/// </summary>
public static class FileAccessTools
{
    /// <summary>Names of the file tools — auto-allowed by AgentRunner whenever a run has attachments.</summary>
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "list-files", "read-file", "search-files", "document-search" };
}

/// <summary>Lists the files attached to the current session (id, name, type, size). No LLM call.</summary>
public sealed class ListFilesTool : IBuiltInTool
{
    public string Name => "list-files";
    public string Description =>
        "Lists the files the user attached to this session. Returns a JSON array of { id, name, type, size, isImage }. Use read-file to actually read one.";

    public Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        var files = AgentRunContext.Current?.Files?.Files;
        if (files is null || files.Count == 0)
            return Task.FromResult(new ToolCallResult { ToolName = Name, Output = "No files are attached to this session." });

        var manifest = files.Select(f => new
        {
            id = f.Id,
            name = f.Name,
            type = f.MediaType,
            size = f.Size,
            isImage = f.IsImage
        });
        return Task.FromResult(new ToolCallResult { ToolName = Name, Output = JsonConvert.SerializeObject(manifest, Formatting.Indented) });
    }
}

/// <summary>
/// Reads one attached file against a query using a fresh reader LLM. Input is JSON
/// <c>{ "file": "&lt;id|name&gt;", "query": "&lt;what to find&gt;" }</c> (a bare file name is also accepted).
/// Text files are summarised; images are described by a vision model.
/// </summary>
public sealed class ReadFileTool : IBuiltInTool
{
    private readonly IModelManager _models;
    private readonly IDocumentTextExtractor _extractor;

    /// <summary>Creates the tool.</summary>
    /// <param name="models">Model registry used to resolve the reader model.</param>
    /// <param name="extractor">The host's registered text extractor (e.g. a PDF extractor); defaults to <see cref="DocumentTextExtractor"/>.</param>
    public ReadFileTool(IModelManager models, IDocumentTextExtractor? extractor = null)
    {
        _models = models;
        _extractor = extractor ?? new DocumentTextExtractor();
    }

    public string Name => "read-file";
    public string Description =>
        "Reads one attached file and answers a question about it via a separate reader model (vision for images). Input: { \"file\": \"<id or name>\", \"query\": \"<what you want to know>\" }.";

    public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        var registry = AgentRunContext.Current?.Files;
        if (registry is null || registry.Files.Count == 0)
            return new ToolCallResult { ToolName = Name, Failed = true, ErrorMessage = "No files are attached to this session." };

        (string fileRef, string query) = ParseInput(input);
        if (string.IsNullOrWhiteSpace(fileRef))
            return new ToolCallResult { ToolName = Name, Failed = true, ErrorMessage = "Specify which file to read: { \"file\": \"<id or name>\", \"query\": \"...\" }." };

        SessionFile? file = registry.Get(fileRef);
        if (file is null)
        {
            string available = string.Join(", ", registry.Files.Select(f => f.Name));
            return new ToolCallResult { ToolName = Name, Failed = true, ErrorMessage = $"No attached file matches '{fileRef}'. Available: {available}." };
        }

        try
        {
            string answer = await FileReader.ReadAsync(_models, file, query, token, _extractor);
            return new ToolCallResult { ToolName = Name, Output = answer };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ToolCallResult { ToolName = Name, Failed = true, ErrorMessage = $"Failed to read '{file.Name}': {ex.Message}" };
        }
    }

    // Accepts { "file": "...", "query": "..." }, or a bare string (treated as the file ref).
    private static (string file, string query) ParseInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return ("", "Summarise this file.");
        string trimmed = input.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var o = JObject.Parse(input);
                string file = (string?)o["file"] ?? (string?)o["name"] ?? (string?)o["id"] ?? "";
                string query = (string?)o["query"] ?? (string?)o["question"] ?? "Summarise this file.";
                return (file.Trim(), string.IsNullOrWhiteSpace(query) ? "Summarise this file." : query);
            }
            catch { /* fall through to bare string */ }
        }
        return (input.Trim(), "Summarise this file.");
    }
}

/// <summary>
/// Runs a query across every attached file via the reader LLM and returns the per-file findings.
/// Input is JSON <c>{ "query": "..." }</c> (or a bare query string).
/// </summary>
public sealed class SearchFilesTool : IBuiltInTool
{
    private readonly IModelManager _models;
    private readonly IDocumentTextExtractor _extractor;

    /// <summary>Creates the tool.</summary>
    /// <param name="models">Model registry used to resolve the reader model.</param>
    /// <param name="extractor">The host's registered text extractor (e.g. a PDF extractor); defaults to <see cref="DocumentTextExtractor"/>.</param>
    public SearchFilesTool(IModelManager models, IDocumentTextExtractor? extractor = null)
    {
        _models = models;
        _extractor = extractor ?? new DocumentTextExtractor();
    }

    public string Name => "search-files";
    public string Description =>
        "Searches all attached files for information relevant to a query (each file is read by a separate reader model). Input: { \"query\": \"<what you are looking for>\" }.";

    public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        var registry = AgentRunContext.Current?.Files;
        if (registry is null || registry.Files.Count == 0)
            return new ToolCallResult { ToolName = Name, Failed = true, ErrorMessage = "No files are attached to this session." };

        string query = ParseQuery(input);
        if (string.IsNullOrWhiteSpace(query))
            return new ToolCallResult { ToolName = Name, Failed = true, ErrorMessage = "Provide a query: { \"query\": \"...\" }." };

        var sb = new StringBuilder();
        foreach (var file in registry.Files)
        {
            token.ThrowIfCancellationRequested();
            sb.AppendLine($"## {file.Name}");
            try { sb.AppendLine(await FileReader.ReadAsync(_models, file, query, token, _extractor)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { sb.AppendLine($"(could not read: {ex.Message})"); }
            sb.AppendLine();
        }
        return new ToolCallResult { ToolName = Name, Output = sb.ToString().TrimEnd() };
    }

    private static string ParseQuery(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string trimmed = input.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try { return ((string?)JObject.Parse(input)["query"] ?? "").Trim(); }
            catch { /* fall through */ }
        }
        return input.Trim();
    }
}

/// <summary>Shared reader-LLM logic: resolves a reader model and reads a file against a query in a fresh context.</summary>
internal static class FileReader
{
    // Known good readers (all vision-capable) tried in order before falling back to any usable model.
    private static readonly string[] PreferredReaders =
        { "gemini-2-5-flash", "gemini-1-5-flash", "gpt-4o-mini", "claude-3-5-sonnet" };

    // Cap text injected into a single reader call; very large files are truncated with a note.
    private const int MaxTextChars = 120_000;

    public static async Task<string> ReadAsync(IModelManager models, SessionFile file, string query, CancellationToken token,
        IDocumentTextExtractor? extractor = null)
    {
        ModelProfile? reader = ResolveReaderModel(models, needVision: file.IsImage);
        if (reader?.Provider?.InferenceClient is null)
            return $"(No reader model is available to read '{file.Name}'.)";

        const string system =
            "You are a precise file-reading assistant working on behalf of another AI agent. Read the provided "
            + "file and answer the agent's request faithfully and concisely. Quote concrete details (figures, names, "
            + "code, exact wording) where relevant. If the file does not address the request, say so plainly.";

        List<Message> messages;
        if (file.IsImage)
        {
            var user = new Message("user",
                $"File: {file.Name} ({file.MediaType})\nRequest: {query}",
                new List<MessageImage> { new(file.MediaType, file.AsBase64()) });
            messages = new List<Message> { new("system", system), user };
        }
        else
        {
            string? body = ExtractBody(file, extractor ?? new DocumentTextExtractor());
            if (body is null) return $"(Unsupported document format: {file.MediaType}. Register a format-aware extractor for document-search.)";
            messages = new List<Message>
            {
                new("system", system),
                new("user", $"File: {file.Name} ({file.MediaType})\nRequest: {query}\n\n--- FILE CONTENT ---\n{body}")
            };
        }

        CompletionResult result = await reader.Provider.InferenceClient.GenerateAsync(
            messages: messages,
            model: reader.ModelString,
            temperature: 0.2f,
            maxTokens: 1200,
            guidanceType: GuidanceType.Disabled,
            cancellationToken: token);

        return string.IsNullOrWhiteSpace(result?.Selected)
            ? $"(The reader model returned no content for '{file.Name}'.)"
            : result.Selected.Trim();
    }

    /// <summary>
    /// The text handed to the reader for a non-image file, capped at <see cref="MaxTextChars"/> with an explicit
    /// note. The host's registered extractor goes first (so a host PDF extractor serves read-file exactly as it
    /// serves document-search); a textual file it declines or cannot handle (e.g. one over its size limit) is
    /// then read leniently, decoding only the characters that will be sent. Null when the file is neither.
    /// </summary>
    /// <param name="file">The attached file.</param>
    /// <param name="extractor">The host's registered extractor.</param>
    internal static string? ExtractBody(SessionFile file, IDocumentTextExtractor extractor)
    {
        string? text = null;
        ExceptionDispatchInfo? failure = null;
        try { text = extractor.Extract(file); }
        catch (Exception ex) when (ex is not OperationCanceledException) { failure = ExceptionDispatchInfo.Capture(ex); }
        if (text is not null)
            return text.Length > MaxTextChars
                ? text[..MaxTextChars] + $"\n\n[…truncated — file is {text.Length:N0} characters; showing the first {MaxTextChars:N0}.]"
                : text;
        string? prefix = DocumentTextExtractor.ReadText(file, MaxTextChars, out bool truncated);
        if (prefix is null)
        {
            // Not text either: surface the extractor's own failure rather than a generic "unsupported".
            failure?.Throw();
            return null;
        }
        return truncated
            ? prefix + $"\n\n[…truncated — file is {file.Size:N0} bytes; showing the first {MaxTextChars:N0} characters.]"
            : prefix;
    }

    private static ModelProfile? ResolveReaderModel(IModelManager models, bool needVision)
    {
        if (needVision)
        {
            var flagged = models.GetAll().FirstOrDefault(m => m.Provider?.InferenceClient is not null && m.EffectiveSupportsVision);
            if (flagged is not null) return flagged;
        }

        foreach (var name in PreferredReaders)
        {
            var m = models.Get(name);
            if (m?.Provider?.InferenceClient is not null) return m;
        }

        return models.GetAll().FirstOrDefault(m => m.Provider?.InferenceClient is not null);
    }
}
