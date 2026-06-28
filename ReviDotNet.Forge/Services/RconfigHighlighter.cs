// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Server-side syntax highlighter for ReviDotNet RConfig files (<c>.rcfg</c> / <c>.agent</c> / <c>.pmt</c> /
/// <c>.tool</c>). It mirrors the scopes in <c>ide/revi-syntax/syntaxes/revi.tmLanguage.json</c> — the same
/// grammar Rider/VS Code use — and emits HTML <c>&lt;span class="revi-*"&gt;</c> tokens, coloured by the
/// scheme in <c>wwwroot/app.css</c>. Line-oriented, matching <c>RConfigParser</c>'s real rules: <c>[[section]]</c>
/// vs raw <c>[[_section]]</c> blocks, first-<c>=</c> key/value split, <c>#</c>-comment-at-line-start, the
/// <c>[[_loop]]</c> transition DSL, <c>[[_exin_N]]</c> labels, and <c>{placeholder}</c> tokens.
/// </summary>
public static class RconfigHighlighter
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // [[ name ]] header — name classified raw (leading '_') vs key-value by the caller.
    private static readonly Regex Header =
        new(@"^(\s*)(\[\[)(\s*)([^\]]*?)(\s*)(\]\])(\s*)$", Opts);

    private static readonly Regex Comment = new(@"^\s*#", Opts);

    // key = value (split on the first '='). Groups: indent, key, ws, '=', ws, value.
    private static readonly Regex KeyValue =
        new(@"^(\s*)([A-Za-z0-9_][\w.-]*)(\s*)(=)(\s*)(.*)$", Opts);

    // A bare state-declaration line inside [[_loop]] (only whitespace + a name).
    private static readonly Regex BareState = new(@"^(\s*)([A-Za-z][\w-]*)(\s*)$", Opts);

    // A labelled segment header inside [[_exin_N]], e.g. [Context].
    private static readonly Regex ExinLabel = new(@"^(\s*)(\[)([A-Za-z][^\]]*?)(\])(\s*)$", Opts);

    // ── inline token rules (tried left-to-right, anchored per position) ───────────────────────
    private static readonly (Regex Rx, Func<Match, string> Render)[] ValueRules =
    {
        (new Regex(@"\{[A-Za-z0-9_][A-Za-z0-9 ._-]*\}", Opts),          m => Span("revi-ph", m.Value)),
        (new Regex(@"\b(true|false)\b", Opts),                          m => Span("revi-bool", m.Value)),
        (new Regex(@"\b(environment|disabled)\b", Opts),                m => Span("revi-const", m.Value)),
        (new Regex(@"(?<![\w.-])-?\d+(?:\.\d+)?(?![\w.-])", Opts),       m => Span("revi-num", m.Value)),
    };

    private static readonly (Regex Rx, Func<Match, string> Render)[] PlaceholderRules =
    {
        (new Regex(@"\{[A-Za-z0-9_][A-Za-z0-9 ._-]*\}", Opts),          m => Span("revi-ph", m.Value)),
    };

    private static readonly (Regex Rx, Func<Match, string> Render)[] LoopRules =
    {
        // -> target  (target: a state name, self, or [end])
        (new Regex(@"(->)(\s*)(\[end\]|self\b|[A-Za-z][\w-]*)?", Opts), m =>
            Span("revi-arrow", m.Groups[1].Value) + Enc(m.Groups[2].Value) +
            (m.Groups[3].Success ? Span("revi-state", m.Groups[3].Value) : "")),
        // [when: SIGNAL]
        (new Regex(@"(\[)(\s*)(when)(\s*)(:)(\s*)([A-Za-z][A-Za-z0-9_]*)?(\s*)(\])", Opts), m =>
            Span("revi-punct", m.Groups[1].Value) + Enc(m.Groups[2].Value) +
            Span("revi-when", m.Groups[3].Value) + Enc(m.Groups[4].Value) +
            Span("revi-punct", m.Groups[5].Value) + Enc(m.Groups[6].Value) +
            (m.Groups[7].Success ? Span("revi-signal", m.Groups[7].Value) : "") +
            Enc(m.Groups[8].Value) + Span("revi-punct", m.Groups[9].Value)),
    };

    private enum Mode { KeyValue, Raw, ExampleInput, Loop }

    /// <summary>Highlights RConfig <paramref name="source"/> into an HTML fragment of <c>revi-*</c> spans.</summary>
    public static string ToHtml(string? source)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;

        var sb = new StringBuilder(source.Length * 2);
        var mode = Mode.KeyValue;
        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(HighlightLine(lines[i], ref mode));
        }
        return sb.ToString();
    }

    private static string HighlightLine(string line, ref Mode mode)
    {
        Match h = Header.Match(line);
        if (h.Success)
        {
            string name = h.Groups[4].Value;
            bool raw = name.StartsWith('_');
            mode = !raw ? Mode.KeyValue
                 : name.Equals("_loop", StringComparison.OrdinalIgnoreCase) ? Mode.Loop
                 : name.StartsWith("_exin_", StringComparison.OrdinalIgnoreCase) ? Mode.ExampleInput
                 : Mode.Raw;

            return Enc(h.Groups[1].Value)
                 + Span("revi-punct", h.Groups[2].Value) + Enc(h.Groups[3].Value)
                 + Span(raw ? "revi-raw" : "revi-section", name) + Enc(h.Groups[5].Value)
                 + Span("revi-punct", h.Groups[6].Value) + Enc(h.Groups[7].Value);
        }

        switch (mode)
        {
            case Mode.Raw:
                return ScanInline(line, PlaceholderRules);

            case Mode.ExampleInput:
                Match lbl = ExinLabel.Match(line);
                if (lbl.Success)
                    return Enc(lbl.Groups[1].Value)
                         + Span("revi-punct", lbl.Groups[2].Value)
                         + Span("revi-label", lbl.Groups[3].Value)
                         + Span("revi-punct", lbl.Groups[4].Value) + Enc(lbl.Groups[5].Value);
                return ScanInline(line, PlaceholderRules);

            case Mode.Loop:
                if (Comment.IsMatch(line)) return Span("revi-comment", line);
                Match st = BareState.Match(line);
                if (st.Success)
                    return Enc(st.Groups[1].Value) + Span("revi-state", st.Groups[2].Value) + Enc(st.Groups[3].Value);
                return ScanInline(line, LoopRules);

            default: // KeyValue
                if (Comment.IsMatch(line)) return Span("revi-comment", line);
                Match kv = KeyValue.Match(line);
                if (kv.Success)
                    return Enc(kv.Groups[1].Value)
                         + Span("revi-key", kv.Groups[2].Value) + Enc(kv.Groups[3].Value)
                         + Span("revi-eq", kv.Groups[4].Value) + Enc(kv.Groups[5].Value)
                         + ScanInline(kv.Groups[6].Value, ValueRules);
                return Enc(line);
        }
    }

    /// <summary>
    /// Walks <paramref name="text"/>, emitting the first inline rule that matches at each position (so rule
    /// order is priority) and HTML-encoding everything in between. Non-overlapping by construction.
    /// </summary>
    private static string ScanInline(string text, (Regex Rx, Func<Match, string> Render)[] rules)
    {
        if (text.Length == 0) return string.Empty;
        var sb = new StringBuilder(text.Length);
        int pos = 0;
        while (pos < text.Length)
        {
            string? emitted = null;
            int len = 0;
            foreach (var (rx, render) in rules)
            {
                Match m = rx.Match(text, pos);
                if (m.Success && m.Index == pos && m.Length > 0)
                {
                    emitted = render(m);
                    len = m.Length;
                    break;
                }
            }
            if (emitted != null)
            {
                sb.Append(emitted);
                pos += len;
            }
            else
            {
                sb.Append(Enc(text[pos].ToString()));
                pos++;
            }
        }
        return sb.ToString();
    }

    private static string Span(string cls, string text) => $"<span class=\"{cls}\">{Enc(text)}</span>";
    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
