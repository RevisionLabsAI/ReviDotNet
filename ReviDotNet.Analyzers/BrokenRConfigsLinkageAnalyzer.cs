// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Analyzer REVI005: Validates that model/provider references declared in prompt (.pmt) files
    /// resolve to existing RConfigs/Models and RConfigs/Providers configuration files.
    /// - Reports an Error when a referenced model or provider cannot be found.
    /// The analyzer is conservative and only reports when it can confidently extract a non-default
    /// identifier from the prompt file.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BrokenRConfigsLinkageAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for broken RConfigs linkage.
        /// </summary>
        public const string DiagnosticId = "REVI005";

        private static readonly LocalizableString TitleMissingModel = "Unknown model reference in prompt";
        private static readonly LocalizableString MsgMissingModel = "Model '{0}' referenced by prompt was not found under RConfigs/Models";

        private static readonly LocalizableString TitleMissingProvider = "Unknown provider reference in prompt";
        private static readonly LocalizableString MsgMissingProvider = "Provider '{0}' referenced by prompt was not found under RConfigs/Providers";

        private const string Category = "Configuration";

        private static readonly DiagnosticDescriptor MissingModelRule = new DiagnosticDescriptor(
            DiagnosticId,
            TitleMissingModel,
            MsgMissingModel,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Prompts should reference existing model configurations defined in RConfigs/Models.");

        private static readonly DiagnosticDescriptor MissingProviderRule = new DiagnosticDescriptor(
            DiagnosticId,
            TitleMissingProvider,
            MsgMissingProvider,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Prompts should reference existing provider configurations defined in RConfigs/Providers.");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(MissingModelRule, MissingProviderRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        /// <summary>
        /// Collects available model/provider identifiers from AdditionalFiles and validates prompt references.
        /// </summary>
        /// <param name="context">The compilation analysis context.</param>
        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            ImmutableArray<AdditionalText> files = context.Options.AdditionalFiles;

            HashSet<string> modelNames = BuildAvailableConfigNames(files, isModel: true);
            HashSet<string> providerNames = BuildAvailableConfigNames(files, isModel: false);

            foreach (AdditionalText file in files)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase))
                    continue;

                SourceText? st = file.GetText(context.CancellationToken);
                string content = st?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                // Extract specific non-default identifiers
                ExtractPromptReferences(content, out string? modelRef, out string? providerRef, out LinePositionSpan? modelSpan, out LinePositionSpan? providerSpan);

                if (!string.IsNullOrWhiteSpace(modelRef))
                {
                    if (!modelNames.Contains(NormalizeKey(modelRef!)))
                    {
                        Location loc = CreateLocation(file, modelSpan);
                        Diagnostic d = Diagnostic.Create(MissingModelRule, loc, modelRef);
                        context.ReportDiagnostic(d);
                    }
                }

                if (!string.IsNullOrWhiteSpace(providerRef))
                {
                    if (!providerNames.Contains(NormalizeKey(providerRef!)))
                    {
                        Location loc = CreateLocation(file, providerSpan);
                        Diagnostic d = Diagnostic.Create(MissingProviderRule, loc, providerRef);
                        context.ReportDiagnostic(d);
                    }
                }
            }
        }

        /// <summary>
        /// Builds a case-insensitive set of available model or provider names from AdditionalFiles (.rcfg).
        /// Name sources:
        /// - File stem (without extension)
        /// - Any explicit 'name = X' or 'id = X' key in the file
        /// </summary>
        private static HashSet<string> BuildAvailableConfigNames(ImmutableArray<AdditionalText> files, bool isModel)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AdditionalText f in files)
            {
                string p = f.Path ?? string.Empty;
                if (!p.EndsWith(".rcfg", StringComparison.OrdinalIgnoreCase))
                    continue;

                string normalized = p.Replace('\\', '/');
                string segment = isModel ? "RConfigs/Models/" : "RConfigs/Providers/";
                if (normalized.IndexOf(segment, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // File stem
                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(p);
                    if (!string.IsNullOrWhiteSpace(fileName))
                        set.Add(NormalizeKey(fileName));
                }
                catch
                {
                    // ignore path issues
                }

                // Parse textual id/name keys inside file
                SourceText? st = f.GetText();
                string text = st?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                foreach (Match m in Regex.Matches(text, @"^\s*(name|id)\s*[:=]\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                {
                    string raw = m.Groups[2].Value.Trim();
                    raw = StripQuotes(raw);
                    if (!string.IsNullOrWhiteSpace(raw))
                        set.Add(NormalizeKey(raw));
                }
            }
            return set;
        }

        /// <summary>
        /// Extracts model and provider references from a prompt file using tolerant regex patterns.
        /// Returns spans for better diagnostic locations when available.
        /// Keys supported (case-insensitive):
        /// - model, model_name, model-profile, model_profile
        /// - provider, provider_name, provider-profile, provider_profile
        /// Values of 'default' or 'auto' are ignored.
        /// </summary>
        private static void ExtractPromptReferences(
            string content,
            out string? model,
            out string? provider,
            out LinePositionSpan? modelSpan,
            out LinePositionSpan? providerSpan)
        {
            model = null;
            provider = null;
            modelSpan = null;
            providerSpan = null;

            Regex rx = new Regex(@"^\s*(model(_name|-profile|_profile)?|provider(_name|-profile|_profile)?)\s*[:=]\s*(.+)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            int line = 0;
            int col = 0;
            int idx = 0;
            foreach (Match m in rx.Matches(content))
            {
                string key = m.Groups[1].Value.Trim().ToLowerInvariant();
                string raw = StripQuotes(m.Groups[4].Value.Trim());
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                if (raw.Equals("default", StringComparison.OrdinalIgnoreCase) || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Compute a simple line/column for the value
                idx = m.Groups[4].Index;
                ComputeLineCol(content, idx, out line, out col);
                LinePosition start = new LinePosition(line, col);
                LinePosition end = new LinePosition(line, Math.Max(col, col + Math.Min(raw.Length, 1)));
                LinePositionSpan span = new LinePositionSpan(start, end);

                if (key.StartsWith("model", StringComparison.Ordinal))
                {
                    model = raw;
                    modelSpan = span;
                }
                else if (key.StartsWith("provider", StringComparison.Ordinal))
                {
                    provider = raw;
                    providerSpan = span;
                }
            }
        }

        /// <summary>
        /// Computes line and column for a character index in the given text.
        /// </summary>
        private static void ComputeLineCol(string text, int index, out int line, out int column)
        {
            line = 0;
            column = 0;
            int lastBreak = -1;
            for (int i = 0; i < text.Length && i < index; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    line++;
                    lastBreak = i;
                }
            }
            column = Math.Max(0, index - (lastBreak + 1));
        }

        private static string StripQuotes(string value)
        {
            string v = value.Trim();
            if ((v.StartsWith("\"", StringComparison.Ordinal) && v.EndsWith("\"", StringComparison.Ordinal)) ||
                (v.StartsWith("'", StringComparison.Ordinal) && v.EndsWith("'", StringComparison.Ordinal)))
            {
                return v.Substring(1, v.Length - 2);
            }
            return v;
        }

        private static string NormalizeKey(string key)
        {
            return key.Trim().ToLowerInvariant();
        }

        private static Location CreateLocation(AdditionalText file, LinePositionSpan? span)
        {
            string path = file.Path ?? string.Empty;
            if (span.HasValue)
            {
                return Location.Create(path, new TextSpan(0, 0), span.Value);
            }
            LinePosition start = new LinePosition(0, 0);
            LinePositionSpan lps = new LinePositionSpan(start, start);
            return Location.Create(path, new TextSpan(0, 0), lps);
        }
    }
}
