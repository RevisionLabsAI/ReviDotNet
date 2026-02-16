using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Analyzer REVI040: Validates schema and basic constraints for model profile .rcfg files
    /// under RConfigs/Models (both Inference and Embedding).
    ///
    /// Rules (initial minimal pass):
    /// - Require a [[general]] section with non-empty keys: name, model-string, provider-name.
    /// - Optional: enabled should be boolean when present.
    /// - Optional [[settings]]: if present, validate numeric non-negative token-limit; tier in {A,B,C}.
    /// - Optional [[override-tuning]]: if present, numeric sampling values must be parseable if not 'disabled'.
    ///
    /// Diagnostics are reported with precise file/line locations when possible.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ModelProfileSchemaAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for model profile schema violations.
        /// </summary>
        public const string DiagnosticId = "REVI040";

        private const string Category = "Configuration";

        private static readonly DiagnosticDescriptor ErrorRule = new DiagnosticDescriptor(
            DiagnosticId,
            "Model profile schema violation",
            "Invalid value '{0}' for '{1}'{2}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Model .rcfg files must follow documented schema. Required keys must be present and valid; enums and numeric ranges must be respected.");

        private static readonly DiagnosticDescriptor WarningRule = new DiagnosticDescriptor(
            DiagnosticId,
            "Model profile advisory",
            "Suspicious value '{0}' for '{1}'{2}",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Model .rcfg contains values that are unusual or out of recommended bounds.");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(ErrorRule, WarningRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        /// <summary>
        /// Scan AdditionalFiles for model .rcfg files and validate.
        /// </summary>
        /// <param name="context">Compilation analysis context.</param>
        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            foreach (AdditionalText file in context.Options.AdditionalFiles)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".rcfg", StringComparison.OrdinalIgnoreCase))
                    continue;

                string normalized = path.Replace('\\', '/');
                if (normalized.IndexOf("RConfigs/Models/", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                SourceText? st = file.GetText(context.CancellationToken);
                string content = st?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                List<Line> lines = BuildLines(content);
                RcfgDoc doc = Parse(lines);

                // Required [[general]] keys
                ValidateRequiredNonEmpty(context, file, doc, section: "general", key: "name");
                ValidateRequiredNonEmpty(context, file, doc, section: "general", key: "model-string");
                ValidateRequiredNonEmpty(context, file, doc, section: "general", key: "provider-name");

                // Optional typing checks
                if (TryGet(doc, "general", "enabled", out RcfgValue enabled))
                {
                    string raw = enabled.Raw.Trim();
                    if (!IsBool(raw))
                    {
                        ReportError(context, file, enabled.Line, raw, "general.enabled", suffix: " (expected boolean)");
                    }
                }

                if (TryGet(doc, "settings", "tier", out RcfgValue tier))
                {
                    string raw = tier.Raw.Trim();
                    if (!new[] { "a", "b", "c" }.Contains(raw.ToLowerInvariant()))
                    {
                        ReportError(context, file, tier.Line, raw, "settings.tier", suffix: " (allowed: A, B, C)");
                    }
                }

                // Optional: settings.supports-prompt-completion must be boolean if present
                if (TryGet(doc, "settings", "supports-prompt-completion", out RcfgValue sspc))
                {
                    if (!IsBool(sspc.Raw.Trim()))
                    {
                        ReportError(context, file, sspc.Line, sspc.Raw.Trim(), "settings.supports-prompt-completion", suffix: " (expected boolean)");
                    }
                }

                // Optional: settings.supports-response-completion must be boolean if present
                if (TryGet(doc, "settings", "supports-response-completion", out RcfgValue ssrc))
                {
                    if (!IsBool(ssrc.Raw.Trim()))
                    {
                        ReportError(context, file, ssrc.Line, ssrc.Raw.Trim(), "settings.supports-response-completion", suffix: " (expected boolean)");
                    }
                }

                if (TryGet(doc, "settings", "token-limit", out RcfgValue tokenLimit))
                {
                    if (!int.TryParse(tokenLimit.Raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tl) || tl < 0)
                    {
                        ReportWarning(context, file, tokenLimit.Line, tokenLimit.Raw.Trim(), "settings.token-limit", suffix: " (>= 0)");
                    }
                }

                // Override-tuning numeric advisories
                ValidateMaybeNumber(context, file, doc, "override-tuning", "temperature");
                ValidateMaybeNumber(context, file, doc, "override-tuning", "top-k");
                ValidateMaybeNumber(context, file, doc, "override-tuning", "top-p");
                ValidateMaybeNumber(context, file, doc, "override-tuning", "min-p");
                ValidateMaybeNumber(context, file, doc, "override-tuning", "presence-penalty");
                ValidateMaybeNumber(context, file, doc, "override-tuning", "frequency-penalty");
                ValidateMaybeNumber(context, file, doc, "override-tuning", "repetition-penalty");
            }
        }

        private static void ValidateRequiredNonEmpty(CompilationAnalysisContext context, AdditionalText file, RcfgDoc doc, string section, string key)
        {
            if (!TryGet(doc, section, key, out RcfgValue v))
            {
                // Put diagnostic at file start when the key/section is absent.
                ReportError(context, file, 1, "<missing>", section + "." + key, suffix: " (required)");
                return;
            }

            string raw = StripQuotes(v.Raw).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                ReportError(context, file, v.Line, v.Raw, section + "." + key, suffix: string.Empty);
            }
        }

        private static void ValidateMaybeNumber(CompilationAnalysisContext context, AdditionalText file, RcfgDoc doc, string section, string key)
        {
            if (!TryGet(doc, section, key, out RcfgValue v))
                return;

            string raw = v.Raw.Trim();
            if (raw.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                return;

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                ReportWarning(context, file, v.Line, raw, section + "." + key, suffix: " (number or 'disabled')");
            }
        }

        private static void ReportError(CompilationAnalysisContext context, AdditionalText file, int line, string value, string key, string suffix)
        {
            Location loc = CreateFileLineLocation(file.Path ?? string.Empty, Math.Max(line, 1));
            Diagnostic d = Diagnostic.Create(ErrorRule, loc, value, key, suffix);
            context.ReportDiagnostic(d);
        }

        private static void ReportWarning(CompilationAnalysisContext context, AdditionalText file, int line, string value, string key, string suffix)
        {
            Location loc = CreateFileLineLocation(file.Path ?? string.Empty, Math.Max(line, 1));
            Diagnostic d = Diagnostic.Create(WarningRule, loc, value, key, suffix);
            context.ReportDiagnostic(d);
        }

        private static bool TryGet(RcfgDoc doc, string section, string key, out RcfgValue value)
        {
            string sec = section.ToLowerInvariant();
            string ky = key.ToLowerInvariant();
            if (doc.Sections.TryGetValue(sec, out Dictionary<string, RcfgValue> dict) && dict.TryGetValue(ky, out RcfgValue v))
            {
                value = v;
                return true;
            }
            value = default;
            return false;
        }

        private static List<Line> BuildLines(string content)
        {
            List<Line> lines = new List<Line>();
            using (StringReader reader = new StringReader(content))
            {
                int i = 0;
                while (true)
                {
                    string? text = reader.ReadLine();
                    if (text == null) break;
                    i++;
                    lines.Add(new Line(i, text));
                }
            }
            return lines;
        }

        private static RcfgDoc Parse(List<Line> lines)
        {
            Dictionary<string, Dictionary<string, RcfgValue>> sections = new Dictionary<string, Dictionary<string, RcfgValue>>(StringComparer.OrdinalIgnoreCase);
            string current = string.Empty;
            Regex header = new Regex(@"^\n?\t?\u0020*\[\[(.+)\]\]\u0020*$", RegexOptions.IgnoreCase);
            Regex kv = new Regex(@"^\s*([^#;\n=:]+)\s*[:=]\s*(.*)$", RegexOptions.IgnoreCase);

            foreach (Line line in lines)
            {
                string text = line.Text;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (header.IsMatch(text))
                {
                    Match m = header.Match(text);
                    current = m.Groups[1].Value.Trim().ToLowerInvariant();
                    if (!sections.ContainsKey(current))
                    {
                        sections[current] = new Dictionary<string, RcfgValue>(StringComparer.OrdinalIgnoreCase);
                    }
                    continue;
                }

                if (kv.IsMatch(text))
                {
                    if (string.IsNullOrEmpty(current))
                        continue;

                    Match m = kv.Match(text);
                    string key = m.Groups[1].Value.Trim().ToLowerInvariant();
                    string raw = m.Groups[2].Value.Trim();
                    sections[current][key] = new RcfgValue(raw, line.Index);
                }
            }

            return new RcfgDoc(sections);
        }

        private static string StripQuotes(string value)
        {
            string v = value.Trim();
            if ((v.StartsWith("\"", StringComparison.Ordinal) && v.EndsWith("\"", StringComparison.Ordinal)) ||
                (v.StartsWith("'", StringComparison.Ordinal) && v.EndsWith("'", StringComparison.Ordinal)))
            {
                return v.Substring(1, Math.Max(0, v.Length - 2));
            }
            return v;
        }

        private static bool IsBool(string raw)
        {
            string v = raw.Trim().ToLowerInvariant();
            return v is "true" or "false";
        }

        private static Location CreateFileLineLocation(string filePath, int line)
        {
            LinePosition start = new LinePosition(Math.Max(0, line - 1), 0);
            LinePosition end = start;
            return Location.Create(filePath, new TextSpan(0, 0), new LinePositionSpan(start, end));
        }

        /// <summary>
        /// Parsed line entry.
        /// </summary>
        private readonly struct Line
        {
            /// <summary>
            /// 1-based line index.
            /// </summary>
            public int Index { get; }

            /// <summary>
            /// Line text.
            /// </summary>
            public string Text { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="Line"/> struct.
            /// </summary>
            /// <param name="index">1-based index of the line.</param>
            /// <param name="text">Text content.</param>
            public Line(int index, string text)
            {
                Index = index;
                Text = text;
            }
        }

        /// <summary>
        /// Value in a section with original raw string and source line.
        /// </summary>
        private readonly struct RcfgValue
        {
            /// <summary>
            /// Raw value (unparsed).
            /// </summary>
            public string Raw { get; }

            /// <summary>
            /// 1-based line index.
            /// </summary>
            public int Line { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="RcfgValue"/> struct.
            /// </summary>
            /// <param name="raw">Raw string.</param>
            /// <param name="line">1-based line where value is declared.</param>
            public RcfgValue(string raw, int line)
            {
                Raw = raw;
                Line = line;
            }
        }

        /// <summary>
        /// Parsed document with case-insensitive sections and keys.
        /// </summary>
        private sealed class RcfgDoc
        {
            /// <summary>
            /// Sections map.
            /// </summary>
            public Dictionary<string, Dictionary<string, RcfgValue>> Sections { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="RcfgDoc"/> class.
            /// </summary>
            /// <param name="sections">Sections map.</param>
            public RcfgDoc(Dictionary<string, Dictionary<string, RcfgValue>> sections)
            {
                Sections = sections;
            }
        }
    }
}
