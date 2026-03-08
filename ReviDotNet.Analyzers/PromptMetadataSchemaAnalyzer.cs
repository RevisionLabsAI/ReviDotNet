// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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
    /// Analyzer REVI006 (implements REVI008 spec): validates <c>.pmt</c> prompt files' metadata and schema
    /// against <c>ReviDotNet.Core/Docs/prompt-files.md</c>.
    ///
    /// Checks performed:
    /// - Structural errors (Error):
    ///   - Missing required sections <c>[[information]]</c> or required keys (<c>name</c>).
    ///   - Empty <c>information.name</c>.
    ///   - Invalid enum values for <c>settings.guidance-schema-type</c>, <c>settings.min-tier</c>, <c>settings.completion-type</c>.
    ///   - Non-integer <c>information.version</c>.
    /// - Soft bounds (Warning):
    ///   - <c>tuning.temperature</c> not in [0, 2].
    ///   - <c>tuning.top-p</c> not in (0, 1] (allow 0 only to flag as warning).
    ///   - <c>tuning.min-p</c> not in [0, 1].
    ///   - <c>tuning.presence-penalty</c>, <c>tuning.frequency-penalty</c> outside [-2, 2].
    ///   - <c>tuning.repetition-penalty</c> <= 0.
    ///   - <c>settings.timeout</c> negative.
    /// Unknown keys are reported as Warning to aid hygiene.
    ///
    /// The analyzer scans <see cref="AnalyzerOptions.AdditionalFiles"/> for <c>.pmt</c> files and reports diagnostics
    /// at the most specific location possible (the key line when available, otherwise the file).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PromptMetadataSchemaAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID exposed for this analyzer.
        /// </summary>
        public const string DiagnosticId = "REVI006";

        private const string Category = "Configuration";

        private static readonly LocalizableString TitleMissingRequired = "Invalid .pmt metadata";
        private static readonly LocalizableString MessageMissingRequired = "Prompt file is missing required {0}: {1}";
        private static readonly LocalizableString DescriptionMissingRequired = "Prompt .pmt files must contain required sections/keys per documentation.";

        private static readonly LocalizableString TitleInvalidEnum = "Invalid option value";
        private static readonly LocalizableString MessageInvalidEnum = "'{0}' is not a valid value for {1}. Allowed: {2}";
        private static readonly LocalizableString DescriptionInvalidEnum = "Option must be one of the documented values.";

        private static readonly LocalizableString TitleOutOfRange = "Value out of recommended range";
        private static readonly LocalizableString MessageOutOfRange = "Value '{0}' for {1} is outside recommended range {2}";
        private static readonly LocalizableString DescriptionOutOfRange = "Adjust value to be within documented bounds.";

        private static readonly LocalizableString TitleUnknownKey = "Unknown key in section";
        private static readonly LocalizableString MessageUnknownKey = "Key '{0}' is not recognized in section {1}";
        private static readonly LocalizableString DescriptionUnknownKey = 
            "Remove or rename unknown keys to match the documented schema in prompt-files.md.";

        private static readonly DiagnosticDescriptor MissingRule = new DiagnosticDescriptor(
            DiagnosticId,
            TitleMissingRequired,
            MessageMissingRequired,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: DescriptionMissingRequired);

        private static readonly DiagnosticDescriptor InvalidEnumRule = new DiagnosticDescriptor(
            DiagnosticId,
            TitleInvalidEnum,
            MessageInvalidEnum,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: DescriptionInvalidEnum);

        private static readonly DiagnosticDescriptor OutOfRangeRule = new DiagnosticDescriptor(
            DiagnosticId,
            TitleOutOfRange,
            MessageOutOfRange,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: DescriptionOutOfRange);

        private static readonly DiagnosticDescriptor UnknownKeyRule = new DiagnosticDescriptor(
            DiagnosticId,
            TitleUnknownKey,
            MessageUnknownKey,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: DescriptionUnknownKey);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(MissingRule, InvalidEnumRule, OutOfRangeRule, UnknownKeyRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            // File-content analyzers in Roslyn typically register via CompilationStartAction and then process AdditionalFiles.
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            ImmutableArray<AdditionalText> files = context.Options.AdditionalFiles;
            foreach (AdditionalText file in files)
            {
                string path = file.Path;
                if (!path.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SourceText? text = file.GetText(context.CancellationToken);
                if (text == null)
                {
                    // Fallback: report at file location-level if needed.
                    ValidateFile(context, file, new List<PmtLine>(), new Dictionary<string, PmtValue>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, PmtValue>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, PmtValue>(StringComparer.OrdinalIgnoreCase), new List<(string section, string key, PmtValue val)>(), null);
                    continue;
                }

                List<PmtLine> lines = BuildLines(text.ToString());
                PmtDocument doc = Parse(lines);
                ValidateFile(context, file, lines, doc.Information, doc.Settings, doc.Tuning, doc.UnknownKeys, doc.SectionSpans);
            }
        }

        private static void ValidateFile(
            CompilationAnalysisContext context,
            AdditionalText file,
            List<PmtLine> lines,
            Dictionary<string, PmtValue> information,
            Dictionary<string, PmtValue> settings,
            Dictionary<string, PmtValue> tuning,
            List<(string section, string key, PmtValue val)> unknownKeys,
            Dictionary<string, (int startLine, int endLine)>? sectionSpans)
        {
            // Helper to locate a specific key; if not found, use section span start.
            Location GetKeyLocation(string section, string key)
            {
                if (sectionSpans != null && sectionSpans.TryGetValue(section, out (int startLine, int endLine) span))
                {
                    LinePosition pos = new LinePosition(span.startLine, 0);
                    LinePositionSpan lps = new LinePositionSpan(pos, pos);
                    return Location.Create(file.Path, textSpan: default, lineSpan: lps);
                }

                return Location.Create(file.Path, default, default);
            }

            // Required: information.name (non-empty)
            if (!information.TryGetValue("name", out PmtValue nameVal) || string.IsNullOrWhiteSpace(nameVal.Raw))
            {
                Location loc = information.TryGetValue("name", out PmtValue existing)
                    ? CreateFileLineLocation(file.Path, existing.Line)
                    : GetKeyLocation("information", "name");
                Diagnostic d = Diagnostic.Create(MissingRule, loc, "key", "information.name");
                context.ReportDiagnostic(d);
            }

            // information.version should be integer if provided
            if (information.TryGetValue("version", out PmtValue versionVal))
            {
                if (!int.TryParse(versionVal.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int _))
                {
                    Diagnostic d = Diagnostic.Create(MissingRule, CreateFileLineLocation(file.Path, versionVal.Line), "integer", "information.version");
                    context.ReportDiagnostic(d);
                }
            }

            // Enums in settings
            if (settings.TryGetValue("guidance-schema-type", out PmtValue gst))
            {
                string[] allowed = { "disabled", "default", "regex-manual", "regex-auto", "json-manual", "json-auto", "gnbf-manual", "gnbf-auto" };
                string norm(string s) => s.Replace("-", string.Empty).ToLowerInvariant();
                HashSet<string> allowedNorm = new HashSet<string>(allowed.Select(norm));
                string rawNorm = norm(gst.Raw);
                // Accept common camel/PascalCase variants (e.g., JsonAuto, RegexManual, GnbfAuto)
                if (!allowedNorm.Contains(rawNorm))
                {
                    string allowStr = string.Join(", ", allowed);
                    Diagnostic d = Diagnostic.Create(InvalidEnumRule, CreateFileLineLocation(file.Path, gst.Line), gst.Raw, "settings.guidance-schema-type", allowStr);
                    context.ReportDiagnostic(d);
                }
            }

            if (settings.TryGetValue("min-tier", out PmtValue tier))
            {
                string[] allowed = { "A", "B", "C" };
                if (!allowed.Contains(tier.Raw, StringComparer.OrdinalIgnoreCase))
                {
                    string allowStr = string.Join(", ", allowed);
                    Diagnostic d = Diagnostic.Create(InvalidEnumRule, CreateFileLineLocation(file.Path, tier.Line), tier.Raw, "settings.min-tier", allowStr);
                    context.ReportDiagnostic(d);
                }
            }

            if (settings.TryGetValue("completion-type", out PmtValue ct))
            {
                string[] allowed = { "auto", "chat-only", "prompt-only", "prompt-chat-one", "prompt-chat-multi" };
                string Norm(string s) => s.Replace("-", string.Empty).ToLowerInvariant();
                HashSet<string> allowedNorm = new HashSet<string>(allowed.Select(Norm));
                string rawNorm = Norm(ct.Raw);
                if (!allowedNorm.Contains(rawNorm))
                {
                    string allowStr = string.Join(", ", allowed);
                    Diagnostic d = Diagnostic.Create(InvalidEnumRule, CreateFileLineLocation(file.Path, ct.Line), ct.Raw, "settings.completion-type", allowStr);
                    context.ReportDiagnostic(d);
                }
            }

            // Numeric bounds warnings
            if (tuning.TryGetValue("temperature", out PmtValue temp))
            {
                if (TryParseFloat(temp.Raw, out double val) && (val < 0.0 || val > 2.0))
                {
                    Diagnostic d = Diagnostic.Create(OutOfRangeRule, CreateFileLineLocation(file.Path, temp.Line), temp.Raw, "tuning.temperature", "[0, 2]");
                    context.ReportDiagnostic(d);
                }
            }
            if (tuning.TryGetValue("top-p", out PmtValue topP))
            {
                if (TryParseFloat(topP.Raw, out double val) && (val < 0.0 || val > 1.0))
                {
                    Diagnostic d = Diagnostic.Create(OutOfRangeRule, CreateFileLineLocation(file.Path, topP.Line), topP.Raw, "tuning.top-p", "[0, 1]");
                    context.ReportDiagnostic(d);
                }
            }
            if (tuning.TryGetValue("min-p", out PmtValue minP))
            {
                if (TryParseFloat(minP.Raw, out double val) && (val < 0.0 || val > 1.0))
                {
                    Diagnostic d = Diagnostic.Create(OutOfRangeRule, CreateFileLineLocation(file.Path, minP.Line), minP.Raw, "tuning.min-p", "[0, 1]");
                    context.ReportDiagnostic(d);
                }
            }
            if (tuning.TryGetValue("presence-penalty", out PmtValue pp))
            {
                if (TryParseFloat(pp.Raw, out double val) && (val < -2.0 || val > 2.0))
                {
                    Diagnostic d = Diagnostic.Create(OutOfRangeRule, CreateFileLineLocation(file.Path, pp.Line), pp.Raw, "tuning.presence-penalty", "[-2, 2]");
                    context.ReportDiagnostic(d);
                }
            }
            if (tuning.TryGetValue("frequency-penalty", out PmtValue fp))
            {
                if (TryParseFloat(fp.Raw, out double val) && (val < -2.0 || val > 2.0))
                {
                    Diagnostic d = Diagnostic.Create(OutOfRangeRule, CreateFileLineLocation(file.Path, fp.Line), fp.Raw, "tuning.frequency-penalty", "[-2, 2]");
                    context.ReportDiagnostic(d);
                }
            }
            if (tuning.TryGetValue("repetition-penalty", out PmtValue rp))
            {
                if (TryParseFloat(rp.Raw, out double val) && (val <= 0.0))
                {
                    Diagnostic d = Diagnostic.Create(OutOfRangeRule, CreateFileLineLocation(file.Path, rp.Line), rp.Raw, "tuning.repetition-penalty", "> 0");
                    context.ReportDiagnostic(d);
                }
            }

            if (settings.TryGetValue("timeout", out PmtValue timeout))
            {
                // Timeout can be integer or string; flag negative if integer
                if (int.TryParse(timeout.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tVal) && tVal < 0)
                {
                    Diagnostic d = Diagnostic.Create(OutOfRangeRule, CreateFileLineLocation(file.Path, timeout.Line), timeout.Raw, "settings.timeout", ">= 0");
                    context.ReportDiagnostic(d);
                }
            }

            // Unknown keys hygiene
            foreach ((string section, string key, PmtValue val) item in unknownKeys)
            {
                Diagnostic d = Diagnostic.Create(UnknownKeyRule, CreateFileLineLocation(file.Path, item.val.Line), item.key, item.section);
                context.ReportDiagnostic(d);
            }
        }

        private static bool TryParseFloat(string raw, out double value)
        {
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static List<PmtLine> BuildLines(string content)
        {
            string[] rawLines = content.Split(new[] {"\r\n", "\n"}, StringSplitOptions.None);
            List<PmtLine> lines = new List<PmtLine>();
            for (int i = 0; i < rawLines.Length; i++)
            {
                string text = rawLines[i];
                lines.Add(new PmtLine(i, text));
            }
            return lines;
        }

        private static PmtDocument Parse(List<PmtLine> lines)
        {
            // Simple tolerant parser for the INI-like format.
            // Sections we track for key validation: information, settings, tuning.
            Dictionary<string, PmtValue> information = new Dictionary<string, PmtValue>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, PmtValue> settings = new Dictionary<string, PmtValue>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, PmtValue> tuning = new Dictionary<string, PmtValue>(StringComparer.OrdinalIgnoreCase);
            List<(string section, string key, PmtValue val)> unknown = new List<(string section, string key, PmtValue val)>();
            Dictionary<string, (int startLine, int endLine)> sectionSpans = new Dictionary<string, (int startLine, int endLine)>(StringComparer.OrdinalIgnoreCase);

            string? currentSection = null;
            bool rawMode = false;

            // Known keys for hygiene check
            HashSet<string> infoKeys = new HashSet<string>(new[] { "name", "version" }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> settingsKeys = new HashSet<string>(new[]
            {
                "filter", "chain-of-thought", "request-json", "guidance-schema-type", "require-valid-output",
                "retry-attempts", "retry-prompt", "few-shot-examples", "best-of", "max-tokens", "timeout",
                "use-search-grounding", "preferred-models", "blocked-models", "min-tier", "completion-type"
            }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> tuningKeys = new HashSet<string>(new[]
            {
                "temperature", "top-k", "top-p", "min-p", "presence-penalty", "frequency-penalty", "repetition-penalty"
            }, StringComparer.OrdinalIgnoreCase);

            Regex sectionRegex = new Regex(@"^\s*\[\[(?<name>[A-Za-z0-9_\-]+)\]\]\s*$", RegexOptions.Compiled);
            Regex kvRegex = new Regex(@"^\s*(?<key>[A-Za-z0-9_\-]+)\s*=\s*(?<val>.+?)\s*$", RegexOptions.Compiled);
            Regex rawSectionRegex = new Regex(@"^\s*\[\[_[A-Za-z0-9_\-]+\]\]\s*$", RegexOptions.Compiled);

            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (rawSectionRegex.IsMatch(text))
                {
                    currentSection = null; // raw sections are not parsed as key-value
                    rawMode = true;
                    continue;
                }

                Match sectionMatch = sectionRegex.Match(text);
                if (sectionMatch.Success)
                {
                    string name = sectionMatch.Groups["name"].Value;
                    currentSection = name;
                    rawMode = false;
                    sectionSpans[name] = (i, i);
                    continue;
                }

                if (rawMode)
                {
                    continue;
                }

                Match kvMatch = kvRegex.Match(text);
                if (kvMatch.Success && currentSection != null)
                {
                    string key = kvMatch.Groups["key"].Value;
                    string val = kvMatch.Groups["val"].Value.Trim();
                    PmtValue pmtVal = new PmtValue(val, lines[i].Index);

                    if (currentSection.Equals("information", StringComparison.OrdinalIgnoreCase))
                    {
                        if (infoKeys.Contains(key))
                        {
                            information[key] = new PmtValue(pmtVal.Raw, lines[i].Index);
                        }
                        else
                        {
                            unknown.Add(("information", key, pmtVal));
                        }
                    }
                    else if (currentSection.Equals("settings", StringComparison.OrdinalIgnoreCase))
                    {
                        if (settingsKeys.Contains(key))
                        {
                            settings[key] = new PmtValue(pmtVal.Raw, lines[i].Index);
                        }
                        else
                        {
                            unknown.Add(("settings", key, pmtVal));
                        }
                    }
                    else if (currentSection.Equals("tuning", StringComparison.OrdinalIgnoreCase))
                    {
                        if (tuningKeys.Contains(key))
                        {
                            tuning[key] = new PmtValue(pmtVal.Raw, lines[i].Index);
                        }
                        else
                        {
                            unknown.Add(("tuning", key, pmtVal));
                        }
                    }
                }
            }

            return new PmtDocument(information, settings, tuning, unknown, sectionSpans);
        }

        private static Location CreateFileLineLocation(string filePath, int line)
        {
            LinePosition lp = new LinePosition(line, 0);
            LinePositionSpan span = new LinePositionSpan(lp, lp);
            return Location.Create(filePath, default, span);
        }

        private struct PmtLine
        {
            public int Index;
            public string Text;

            public PmtLine(int index, string text)
            {
                this.Index = index;
                this.Text = text ?? string.Empty;
            }
        }

        private struct PmtValue
        {
            public string Raw;
            public int Line;

            public PmtValue(string raw, int line)
            {
                this.Raw = raw ?? string.Empty;
                this.Line = line;
            }
        }

        private sealed class PmtDocument
        {
            public Dictionary<string, PmtValue> Information;
            public Dictionary<string, PmtValue> Settings;
            public Dictionary<string, PmtValue> Tuning;
            public List<(string section, string key, PmtValue val)> UnknownKeys;
            public Dictionary<string, (int startLine, int endLine)> SectionSpans;

            public PmtDocument(
                Dictionary<string, PmtValue> information,
                Dictionary<string, PmtValue> settings,
                Dictionary<string, PmtValue> tuning,
                List<(string section, string key, PmtValue val)> unknownKeys,
                Dictionary<string, (int startLine, int endLine)> sectionSpans)
            {
                this.Information = information;
                this.Settings = settings;
                this.Tuning = tuning;
                this.UnknownKeys = unknownKeys;
                this.SectionSpans = sectionSpans;
            }
        }
    }
}
