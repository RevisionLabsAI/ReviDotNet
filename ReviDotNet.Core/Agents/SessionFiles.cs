// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;

namespace Revi;

/// <summary>
/// A single user-attached file made available to an agent run. The agent never receives the raw
/// bytes in its context — instead it is told the file exists (via a manifest) and reads it through
/// the <c>list-files</c> / <c>read-file</c> tools, which delegate heavy reading to a fresh reader
/// LLM (vision-capable for images). See <see cref="SessionFileRegistry"/>.
/// </summary>
public sealed class SessionFile
{
    /// <summary>Stable short id used by tools to reference the file (alongside <see cref="Name"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Original file name, e.g. <c>report.pdf</c>.</summary>
    public required string Name { get; init; }

    /// <summary>MIME type, e.g. <c>text/plain</c> or <c>image/png</c>.</summary>
    public required string MediaType { get; init; }

    /// <summary>Raw file bytes.</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size => Bytes.LongLength;

    /// <summary>True when the file is an image (<c>image/*</c>) and should be read via a vision model.</summary>
    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes the file as UTF-8 text (for text/document files).</summary>
    public string AsText() => Encoding.UTF8.GetString(Bytes);

    /// <summary>Base64-encodes the bytes (for inline image payloads to a vision model).</summary>
    public string AsBase64() => Convert.ToBase64String(Bytes);
}

/// <summary>
/// The set of files attached to one agent run, threaded through <see cref="AgentRunContext"/> so the
/// file tools can reach them via <see cref="AgentRunContext.Current"/>. Immutable for the run.
/// </summary>
public sealed class SessionFileRegistry
{
    public SessionFileRegistry(IReadOnlyList<SessionFile> files) => Files = files;

    /// <summary>All attached files, in attachment order.</summary>
    public IReadOnlyList<SessionFile> Files { get; }

    /// <summary>Resolves a file by its id or (case-insensitive) name; null if not found.</summary>
    public SessionFile? Get(string idOrName) =>
        Files.FirstOrDefault(f => string.Equals(f.Id, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? Files.FirstOrDefault(f => string.Equals(f.Name, idOrName, StringComparison.OrdinalIgnoreCase));
}
