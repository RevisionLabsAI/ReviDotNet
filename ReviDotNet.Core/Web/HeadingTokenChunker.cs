// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using System.Text.RegularExpressions;

namespace Revi;

/// <summary>
/// Heading-aware, token-bounded Markdown chunker. Splits first on Markdown headings (the highest-ROI
/// strategy on header-rich Markdown) and prepends each chunk with its heading breadcrumb so an
/// embedded slice retains its context. Sections that still exceed the token budget are split
/// recursively on paragraph/whitespace boundaries with a configurable overlap. Token counts use the
/// shared character-based estimate (<see cref="Util.EstTokenCountFromCharCount"/>) so chunking stays
/// synchronous and cheap.
/// </summary>
public sealed class HeadingTokenChunker : IContentChunker
{
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex ParagraphSplit = new(@"\n\s*\n", RegexOptions.Compiled);

    /// <inheritdoc/>
    public IReadOnlyList<WebChunk> Chunk(string markdown, WebMetadata metadata, ChunkOptions options)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];

        List<(string Trail, string Body)> sections = SplitIntoSections(markdown, metadata?.Title);
        int maxChars = Math.Max(64, Util.EstCharCountForMaxTokens(options.MaxTokens));
        int overlapChars = Math.Max(0, Math.Min(maxChars / 2, Util.EstCharCountFromTokenCount(options.OverlapTokens)));

        List<WebChunk> chunks = [];
        foreach ((string trail, string body) in sections)
        {
            List<string> pieces = Util.EstTokenCountFromCharCount(body.Length) <= options.MaxTokens
                ? [body]
                : SplitWithOverlap(body, maxChars, overlapChars);

            foreach (string piece in pieces)
            {
                string text = piece.Trim();
                if (text.Length == 0) continue;

                string finalText = options.PrependHeadingTrail && !string.IsNullOrEmpty(trail)
                    ? trail + "\n\n" + text
                    : text;

                chunks.Add(new WebChunk
                {
                    Index = chunks.Count,
                    HeadingTrail = string.IsNullOrEmpty(trail) ? null : trail,
                    Text = finalText,
                    EstimatedTokens = Util.EstTokenCountFromCharCount(finalText.Length),
                });
            }
        }

        return chunks;
    }

    /// <summary>
    /// Walks the Markdown line by line, splitting at ATX headings and tracking the heading stack to
    /// build a breadcrumb trail for each section. Fenced code blocks are skipped so a <c>#</c> inside
    /// code is not mistaken for a heading. The heading line itself is captured in the trail, not the body.
    /// </summary>
    private static List<(string Trail, string Body)> SplitIntoSections(string markdown, string? title)
    {
        List<(string, string)> sections = [];
        string?[] stack = new string?[7]; // 1..6
        List<string> bodyLines = [];
        string currentTrail = MakeTrail(stack, title);
        bool inFence = false;

        void Flush()
        {
            string text = string.Join("\n", bodyLines).Trim();
            if (text.Length > 0) sections.Add((currentTrail, text));
            bodyLines.Clear();
        }

        foreach (string line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                bodyLines.Add(line);
                continue;
            }

            if (!inFence)
            {
                Match m = HeadingLine.Match(line);
                if (m.Success)
                {
                    Flush();
                    int level = m.Groups[1].Value.Length;
                    string heading = m.Groups[2].Value.TrimEnd('#', ' ', '\t').Trim();
                    stack[level] = heading;
                    for (int l = level + 1; l <= 6; l++) stack[l] = null;
                    currentTrail = MakeTrail(stack, title);
                    continue;
                }
            }

            bodyLines.Add(line);
        }

        Flush();
        return sections;
    }

    /// <summary>Joins the document title and the active heading stack into a " &gt; " breadcrumb.</summary>
    private static string MakeTrail(string?[] stack, string? title)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title.Trim());
        for (int l = 1; l <= 6; l++)
            if (!string.IsNullOrWhiteSpace(stack[l])) parts.Add(stack[l]!);
        return string.Join(" > ", parts);
    }

    /// <summary>
    /// Recursively splits an over-budget section: pack paragraphs up to the char budget, carrying a
    /// whitespace-aligned overlap tail into the next piece. A single paragraph larger than the budget
    /// is hard-split on the nearest whitespace.
    /// </summary>
    private static List<string> SplitWithOverlap(string text, int maxChars, int overlapChars)
    {
        List<string> pieces = [];
        StringBuilder buf = new();

        void FlushBuf()
        {
            if (buf.Length == 0) return;
            string piece = buf.ToString().Trim();
            buf.Clear();
            if (piece.Length == 0) return;
            pieces.Add(piece);

            // Seed the next buffer with an overlap tail, aligned to a word boundary.
            if (overlapChars > 0 && piece.Length > overlapChars)
            {
                string tail = piece[^overlapChars..];
                int ws = tail.IndexOf(' ');
                if (ws > 0 && ws < tail.Length - 1) tail = tail[(ws + 1)..];
                buf.Append(tail).Append("\n\n");
            }
        }

        foreach (string paraRaw in ParagraphSplit.Split(text))
        {
            string para = paraRaw.Trim();
            if (para.Length == 0) continue;

            if (para.Length > maxChars)
            {
                FlushBuf();
                buf.Clear(); // do not carry text overlap into a hard-split paragraph
                foreach (string seg in Util.SplitStringByNearestWhitespace(para, maxChars))
                {
                    string s = seg.Trim();
                    if (s.Length > 0) pieces.Add(s);
                }
                continue;
            }

            if (buf.Length > 0 && buf.Length + para.Length + 2 > maxChars)
                FlushBuf();

            buf.Append(para).Append("\n\n");
        }

        FlushBuf();
        return pieces;
    }
}
