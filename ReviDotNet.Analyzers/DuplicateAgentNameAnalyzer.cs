// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// REVI007: Detects duplicate agent logical names among .agent AdditionalFiles.
    /// Mirrors DuplicatePromptNameAnalyzer (REVI004) with .agent extension and RConfigs/Agents/ path.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DuplicateAgentNameAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "REVI007";

        private static readonly LocalizableString Title = "Duplicate agent name";
        private static readonly LocalizableString Message = "Multiple agent files resolve to the same name: '{0}'";
        private static readonly LocalizableString Description = "Ensure each agent has a unique effective name (folder prefix + information_name).";
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            Message,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            ImmutableArray<AdditionalText> files = context.Options.AdditionalFiles;
            Dictionary<string, List<AdditionalText>> byName = new Dictionary<string, List<AdditionalText>>(StringComparer.OrdinalIgnoreCase);

            foreach (AdditionalText file in files)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".agent", StringComparison.OrdinalIgnoreCase))
                    continue;

                SourceText? st = file.GetText(context.CancellationToken);
                string content = st?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                string? infoName = TryParseInformationName(content);
                if (string.IsNullOrEmpty(infoName))
                    continue;

                string prefix = ExtractAgentFolderPrefix(path);
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
                if (kv.Value.Count <= 1)
                    continue;

                foreach (AdditionalText file in kv.Value)
                {
                    Location loc = CreateFileStartLocation(file);
                    context.ReportDiagnostic(Diagnostic.Create(Rule, loc, kv.Key));
                }
            }
        }

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

        private static string ExtractAgentFolderPrefix(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return string.Empty;

            string normalized = fullPath.Replace('\\', '/');
            int idx = normalized.IndexOf("RConfigs/Agents/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return string.Empty;

            int start = idx + "RConfigs/Agents/".Length;
            string afterBase = normalized.Substring(start);
            int lastSlash = afterBase.LastIndexOf('/');
            if (lastSlash <= 0)
                return string.Empty;

            return afterBase.Substring(0, lastSlash + 1).ToLowerInvariant();
        }

        private static Location CreateFileStartLocation(AdditionalText file)
        {
            string path = file.Path ?? string.Empty;
            LinePosition start = new LinePosition(0, 0);
            LinePositionSpan lps = new LinePositionSpan(start, start);
            return Location.Create(path, new TextSpan(0, 0), lps);
        }
    }
}
