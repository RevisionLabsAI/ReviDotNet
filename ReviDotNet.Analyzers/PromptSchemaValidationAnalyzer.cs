// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Analyzer REVI010: cross-validates a <c>.pmt</c> file's <c>[[_schema]]</c> block against its
    /// <c>settings.guidance-schema-type</c> strategy.
    ///
    /// Warnings:
    /// <list type="bullet">
    /// <item>A <c>*-manual</c> strategy with no <c>[[_schema]]</c> section (the manual schema source is missing).</item>
    /// <item>An <c>[[_schema]]</c> section present while the strategy is <c>*-auto</c>/<c>disabled</c>/<c>default</c>/<c>defer</c>/unset (the block is ignored — orphaned).</item>
    /// <item>A <c>json-manual</c> <c>[[_schema]]</c> whose body is not structurally valid JSON (unbalanced braces/brackets, or not an object/array).</item>
    /// </list>
    /// The JSON check is intentionally conservative (structural only) to avoid false positives.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PromptSchemaValidationAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Diagnostic ID exposed for this analyzer.</summary>
        public const string DiagnosticId = "REVI010";

        private const string Category = "Configuration";

        private static readonly LocalizableString TitleMissingSchema = "Manual guidance strategy without a schema";
        private static readonly LocalizableString MessageMissingSchema =
            "guidance-schema-type '{0}' is a manual strategy but the prompt has no [[_schema]] section; no constraint will be applied";

        private static readonly LocalizableString TitleOrphanSchema = "Orphaned [[_schema]] section";
        private static readonly LocalizableString MessageOrphanSchema =
            "[[_schema]] is present but guidance-schema-type {0}, so the schema is ignored; use a *-manual strategy or remove the section";

        private static readonly LocalizableString TitleInvalidJson = "json-manual schema is not valid JSON";
        private static readonly LocalizableString MessageInvalidJson =
            "the [[_schema]] body for json-manual does not look like valid JSON (it must be a brace/bracket-balanced object or array)";

        private static readonly DiagnosticDescriptor MissingSchemaRule = new DiagnosticDescriptor(
            DiagnosticId, TitleMissingSchema, MessageMissingSchema, Category,
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor OrphanSchemaRule = new DiagnosticDescriptor(
            DiagnosticId, TitleOrphanSchema, MessageOrphanSchema, Category,
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor InvalidJsonRule = new DiagnosticDescriptor(
            DiagnosticId, TitleInvalidJson, MessageInvalidJson, Category,
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly Regex SectionHeader =
            new Regex(@"^\s*\[\[(?<name>[A-Za-z0-9_\-]+)\]\]\s*$", RegexOptions.Compiled);
        private static readonly Regex GuidanceKey =
            new Regex(@"^\s*guidance-schema-type\s*=\s*(?<val>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(MissingSchemaRule, OrphanSchemaRule, InvalidJsonRule);

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
                if (!file.Path.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase))
                    continue;

                SourceText? text = file.GetText(context.CancellationToken);
                if (text == null)
                    continue;

                AnalyzeFile(context, file, text.ToString());
            }
        }

        private static void AnalyzeFile(CompilationAnalysisContext context, AdditionalText file, string content)
        {
            string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            string? currentSection = null;
            string? strategyRaw = null;
            int strategyLine = -1;

            bool hasSchema = false;
            int schemaHeaderLine = -1;
            var schemaBody = new System.Text.StringBuilder();
            bool inSchema = false;

            for (int i = 0; i < lines.Length; i++)
            {
                Match header = SectionHeader.Match(lines[i]);
                if (header.Success)
                {
                    currentSection = header.Groups["name"].Value;
                    inSchema = currentSection.Equals("_schema", StringComparison.OrdinalIgnoreCase);
                    if (inSchema) { hasSchema = true; schemaHeaderLine = i; }
                    continue;
                }

                if (inSchema)
                {
                    schemaBody.AppendLine(lines[i]);
                    continue;
                }

                if (string.Equals(currentSection, "settings", StringComparison.OrdinalIgnoreCase) && strategyRaw == null)
                {
                    Match g = GuidanceKey.Match(lines[i]);
                    if (g.Success) { strategyRaw = g.Groups["val"].Value.Trim(); strategyLine = i; }
                }
            }

            string norm = Normalize(strategyRaw);
            bool isManual = norm == "jsonmanual" || norm == "json"
                         || norm == "regexmanual" || norm == "regex"
                         || norm == "gnbfmanual" || norm == "gbnf";
            bool isJsonManual = norm == "jsonmanual" || norm == "json";
            bool isInactive = norm.Length == 0 || norm == "disabled" || norm == "default" || norm == "defer"
                           || norm == "jsonauto" || norm == "regexauto" || norm == "gnbfauto";

            // Manual strategy but no schema source.
            if (isManual && !hasSchema)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingSchemaRule, LineLocation(file.Path, strategyLine >= 0 ? strategyLine : 0), strategyRaw));
            }

            // Schema present but the strategy won't consume it.
            if (hasSchema && isInactive)
            {
                string reason = norm.Length == 0 ? "is unset" : $"= '{strategyRaw}' (not a manual strategy)";
                context.ReportDiagnostic(Diagnostic.Create(
                    OrphanSchemaRule, LineLocation(file.Path, schemaHeaderLine), reason));
            }

            // json-manual schema body must be structurally valid JSON.
            if (isJsonManual && hasSchema && !LooksLikeJson(schemaBody.ToString()))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidJsonRule, LineLocation(file.Path, schemaHeaderLine)));
            }
        }

        private static string Normalize(string? s) =>
            string.IsNullOrWhiteSpace(s) ? string.Empty : s!.Trim().Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();

        /// <summary>
        /// Conservative structural JSON check: the trimmed body must start with '{' or '[' and have its
        /// braces and brackets balanced (ignoring delimiters inside double-quoted strings, honoring \\ escapes).
        /// Catches truncated/unbalanced manual schemas without a full parser; does not validate commas/keys.
        /// </summary>
        private static bool LooksLikeJson(string body)
        {
            string s = body.Trim();
            if (s.Length == 0) return false;
            if (s[0] != '{' && s[0] != '[') return false;

            int curly = 0, square = 0;
            bool inString = false, escaped = false;
            foreach (char c in s)
            {
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"': inString = true; break;
                    case '{': curly++; break;
                    case '}': curly--; break;
                    case '[': square++; break;
                    case ']': square--; break;
                }

                if (curly < 0 || square < 0) return false;
            }

            return curly == 0 && square == 0 && !inString;
        }

        private static Location LineLocation(string filePath, int line)
        {
            LinePosition pos = new LinePosition(Math.Max(0, line), 0);
            return Location.Create(filePath, default, new LinePositionSpan(pos, pos));
        }
    }
}
