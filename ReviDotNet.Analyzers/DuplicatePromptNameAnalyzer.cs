// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Analyzer REVI004: Detects duplicate prompt logical names among .pmt AdditionalFiles.
    /// A prompt's effective name matches Infer/PromptManager rules: lower-cased folder prefix under
    /// RConfigs/Prompts/ (with forward slashes and a trailing slash when present) concatenated with
    /// the information_name value inside the file.
    /// Reports a Warning when multiple distinct files resolve to the same name (case-insensitive).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicatePromptNameAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for duplicate prompt names.
        /// </summary>
        public const string DiagnosticId = "REVI004";

        private static readonly LocalizableString Title = "Duplicate prompt name";
        private static readonly LocalizableString Message = "Multiple prompt files resolve to the same name: '{0}'";
        private static readonly LocalizableString Description = "Ensure each prompt has a unique effective name (folder prefix + information_name).";
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            Message,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        /// <summary>
        /// Scans AdditionalFiles for .pmt prompts and reports duplicates by logical name (case-insensitive).
        /// </summary>
        /// <param name="context">The compilation analysis context.</param>
        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            ImmutableArray<AdditionalText> files = context.Options.AdditionalFiles;
            Dictionary<string, List<AdditionalText>> byName = new Dictionary<string, List<AdditionalText>>(StringComparer.OrdinalIgnoreCase);

            foreach (AdditionalText file in files)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase))
                    continue;

                SourceText? st = file.GetText(context.CancellationToken);
                string content = st?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                string? infoName = TryParseInformationName(content);
                if (string.IsNullOrEmpty(infoName))
                    continue;

                string prefix = ExtractPromptFolderPrefix(path);
                string fullName = prefix + infoName;

                if (!byName.TryGetValue(fullName, out List<AdditionalText>? list))
                {
                    list = new List<AdditionalText>();
                    byName[fullName] = list;
                }
                list.Add(file);
            }

            foreach (KeyValuePair<string, List<AdditionalText>> kv in byName)
            {
                List<AdditionalText> list = kv.Value;
                if (list.Count <= 1)
                    continue;

                foreach (AdditionalText file in list)
                {
                    Location loc = CreateFileStartLocation(file);
                    Diagnostic d = Diagnostic.Create(Rule, loc, kv.Key);
                    context.ReportDiagnostic(d);
                }
            }
        }

        /// <summary>
        /// Attempts to parse the information_name value from a .pmt file content.
        /// Supports either a top-level key or an [[information]] section with a name key.
        /// </summary>
        /// <param name="content">The prompt file content.</param>
        /// <returns>The parsed information_name or null when not found.</returns>
        private static string? TryParseInformationName(string content)
        {
            System.Text.RegularExpressions.Regex rx = new System.Text.RegularExpressions.Regex(@"^\s*information_name\s*[:=]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            System.Text.RegularExpressions.Match m = rx.Match(content);
            string? value;
            if (m.Success)
            {
                value = m.Groups[1].Value.Trim();
            }
            else
            {
                System.Text.RegularExpressions.Regex sectionRx = new System.Text.RegularExpressions.Regex(@"\[\[\s*information\s*\]\](?<body>.*?)(?:\n\s*\[\[|\z)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                System.Text.RegularExpressions.Match s = sectionRx.Match(content);
                if (!s.Success)
                    return null;

                string body = s.Groups["body"].Value;
                System.Text.RegularExpressions.Regex nameRx = new System.Text.RegularExpressions.Regex(@"^\s*name\s*[:=]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                System.Text.RegularExpressions.Match nm = nameRx.Match(body);
                if (!nm.Success)
                    return null;
                value = nm.Groups[1].Value.Trim();
            }

            if ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) ||
                (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal)))
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }

        /// <summary>
        /// Extracts lower-cased forward-slash folder prefix under RConfigs/Prompts/ with trailing slash.
        /// </summary>
        /// <param name="fullPath">The OS path.</param>
        /// <returns>The normalized prefix or empty string.</returns>
        private static string ExtractPromptFolderPrefix(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return string.Empty;

            string normalized = fullPath.Replace('\\', '/');
            int idx = normalized.IndexOf("RConfigs/Prompts/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return string.Empty;

            int start = idx + "RConfigs/Prompts/".Length;
            string afterBase = normalized.Substring(start);
            int lastSlash = afterBase.LastIndexOf('/');
            if (lastSlash <= 0)
                return string.Empty;

            string directories = afterBase.Substring(0, lastSlash + 1);
            return directories.ToLowerInvariant();
        }

        /// <summary>
        /// Creates a file start location for an AdditionalText.
        /// </summary>
        /// <param name="file">The additional file.</param>
        /// <returns>A location at the start of the file.</returns>
        private static Location CreateFileStartLocation(AdditionalText file)
        {
            string path = file.Path ?? string.Empty;
            LinePosition start = new LinePosition(0, 0);
            LinePositionSpan lps = new LinePositionSpan(start, start);
            return Location.Create(path, new TextSpan(0, 0), lps);
        }
    }
}
