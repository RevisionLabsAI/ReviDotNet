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
    /// Analyzer REVI041: Validates provider profile .rcfg files under RConfigs/Providers.
    ///
    /// Checks:
    /// - [[general]] name non-empty; protocol in supported list; api-url non-empty.
    /// - enabled/supports-prompt-completion boolean when present.
    /// - [[guidance]]: supports-guidance boolean; default-guidance-type in allowed list.
    /// - [[limiting]]: integer non-negative values for timeout/delay/retry and simultaneous-requests.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ProviderProfileSchemaAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for provider profile schema violations.
        /// </summary>
        public const string DiagnosticId = "REVI041";

        private const string Category = "Configuration";

        private static readonly DiagnosticDescriptor ErrorRule = new DiagnosticDescriptor(
            DiagnosticId,
            "Provider profile schema violation",
            "Invalid value '{0}' for '{1}'{2}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Provider .rcfg files must follow documented schema. Required keys must be present and valid.");

        private static readonly DiagnosticDescriptor WarningRule = new DiagnosticDescriptor(
            DiagnosticId,
            "Provider profile advisory",
            "Suspicious value '{0}' for '{1}'{2}",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Provider .rcfg file contains values that are unusual or out of recommended bounds.");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(ErrorRule, WarningRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            foreach (AdditionalText file in context.Options.AdditionalFiles)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".rcfg", StringComparison.OrdinalIgnoreCase))
                    continue;

                string normalized = path.Replace('\\', '/');
                if (normalized.IndexOf("RConfigs/Providers/", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                SourceText? st = file.GetText(context.CancellationToken);
                string content = st?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                List<Line> lines = BuildLines(content);
                RcfgDoc doc = Parse(lines);

                // Required general keys
                ValidateRequiredNonEmpty(context, file, doc, "general", "name");
                ValidateRequiredNonEmpty(context, file, doc, "general", "api-url");

                if (TryGet(doc, "general", "protocol", out RcfgValue protocol))
                {
                    string raw = protocol.Raw.Trim();
                    string[] allowed = { "openai", "vllm", "gemini", "llamaapi", "claude" };
                    if (!allowed.Contains(raw.ToLowerInvariant()))
                    {
                        ReportError(context, file, protocol.Line, raw, "general.protocol", suffix: " (allowed: OpenAI, vLLM, Gemini, LLamaAPI, Claude)");
                    }
                }
                else
                {
                    ReportError(context, file, 1, "<missing>", "general.protocol", suffix: " (required)");
                }

                if (TryGet(doc, "general", "enabled", out RcfgValue enabled))
                {
                    if (!IsBool(enabled.Raw))
                    {
                        ReportError(context, file, enabled.Line, enabled.Raw, "general.enabled", suffix: " (expected boolean)");
                    }
                }

                if (TryGet(doc, "general", "supports-prompt-completion", out RcfgValue spc))
                {
                    if (!IsBool(spc.Raw))
                    {
                        ReportError(context, file, spc.Line, spc.Raw, "general.supports-prompt-completion", suffix: " (expected boolean)");
                    }
                }

                if (TryGet(doc, "guidance", "supports-guidance", out RcfgValue sg))
                {
                    if (!IsBool(sg.Raw))
                    {
                        ReportError(context, file, sg.Line, sg.Raw, "guidance.supports-guidance", suffix: " (expected boolean)");
                    }
                }

                if (TryGet(doc, "guidance", "default-guidance-type", out RcfgValue gtype))
                {
                    string raw = gtype.Raw.Trim().ToLowerInvariant();
                    string[] allowed = {
                        "disabled", "default",
                        "regex-manual", "regex-auto",
                        "json-manual", "json-auto",
                        "gnbf-manual", "gnbf-auto"
                    };
                    if (!allowed.Contains(raw))
                    {
                        ReportError(context, file, gtype.Line, gtype.Raw.Trim(), "guidance.default-guidance-type",
                            suffix: " (allowed: disabled, default, regex-manual, regex-auto, json-manual, json-auto, gnbf-manual, gnbf-auto)");
                    }
                }

                // Limiting: all integers >= 0 when present
                ValidateNonNegativeInt(context, file, doc, "limiting", "timeout-seconds");
                ValidateNonNegativeInt(context, file, doc, "limiting", "delay-between-requests-ms");
                ValidateNonNegativeInt(context, file, doc, "limiting", "retry-attempt-limit");
                ValidateNonNegativeInt(context, file, doc, "limiting", "retry-initial-delay-seconds");
                ValidateNonNegativeInt(context, file, doc, "limiting", "simultaneous-requests");
            }
        }

        private static void ValidateRequiredNonEmpty(CompilationAnalysisContext context, AdditionalText file, RcfgDoc doc, string section, string key)
        {
            if (!TryGet(doc, section, key, out RcfgValue v))
            {
                ReportError(context, file, 1, "<missing>", section + "." + key, suffix: " (required)");
                return;
            }
            string raw = StripQuotes(v.Raw).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                ReportError(context, file, v.Line, v.Raw, section + "." + key, suffix: string.Empty);
            }
        }

        private static void ValidateNonNegativeInt(CompilationAnalysisContext context, AdditionalText file, RcfgDoc doc, string section, string key)
        {
            if (!TryGet(doc, section, key, out RcfgValue v))
                return;

            if (!int.TryParse(v.Raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int num) || num < 0)
            {
                ReportWarning(context, file, v.Line, v.Raw.Trim(), section + "." + key, suffix: " (>= 0)");
            }
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

        private static Location CreateFileLineLocation(string filePath, int line)
        {
            LinePosition start = new LinePosition(Math.Max(0, line - 1), 0);
            LinePosition end = start;
            return Location.Create(filePath, new TextSpan(0, 0), new LinePositionSpan(start, end));
        }

        private readonly struct Line
        {
            public int Index { get; }
            public string Text { get; }
            public Line(int index, string text)
            {
                Index = index;
                Text = text;
            }
        }

        private readonly struct RcfgValue
        {
            public string Raw { get; }
            public int Line { get; }
            public RcfgValue(string raw, int line)
            {
                Raw = raw;
                Line = line;
            }
        }

        private sealed class RcfgDoc
        {
            public Dictionary<string, Dictionary<string, RcfgValue>> Sections { get; }
            public RcfgDoc(Dictionary<string, Dictionary<string, RcfgValue>> sections)
            {
                Sections = sections;
            }
        }
    }
}
