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
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Analyzes calls to Infer methods to ensure the specified prompt exists.
    /// This analyzer mirrors the same name-to-file matching rules used by Infer/PromptManager:
    /// - Prompts are loaded from files under an <c>RConfigs/Prompts</c> directory (any depth)
    /// - A prompt's effective name is the lower-cased subdirectory path (with forward slashes and trailing slash)
    ///   concatenated with the prompt's <c>information_name</c> value inside the file
    /// - File name itself is not used for matching
    ///
    /// The analyzer parses AdditionalFiles with <c>.pmt</c> extension to build the set of available prompt names and
    /// verifies that string literals passed as <c>promptName</c> to Infer APIs exist in that set.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class PromptFileExistsAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// The diagnostic ID for missing prompt references.
        /// </summary>
        public const string DiagnosticId = "REVI001";

        private static readonly LocalizableString Title = "Missing Prompt";
        private static readonly LocalizableString MessageFormat = "Prompt '{0}' not found in AdditionalFiles (RConfigs/Prompts)";
        private static readonly LocalizableString Description = "All prompts used in Infer methods must exist in AdditionalFiles and follow the same name resolution as Infer (folder prefix + information_name).";
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <summary>
        /// Initializes the analyzer.
        /// </summary>
        /// <param name="context">The analysis context.</param>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        /// <summary>
        /// Analyzes an invocation expression to check for Infer method calls.
        /// </summary>
        /// <param name="context">The syntax node analysis context.</param>
        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return;

            // Check if the class is "Infer" (or "Revi.Infer")
            ISymbol? resolved = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            IMethodSymbol? symbol = resolved as IMethodSymbol;
            if (symbol == null)
                return;

            if (symbol.ContainingType.Name != "Infer" || (symbol.ContainingType.ContainingNamespace?.Name != "Revi" && symbol.ContainingType.ContainingNamespace?.Name != "ReviDotNet"))
                return;

            // List of methods that take a promptName as the first argument
            string[] targetMethods = { "ToObject", "ToEnum", "ToString", "ToStringList", "ToStringListLimited", "ToBool", "ToJObject", "Completion" };
            if (!targetMethods.Contains(symbol.Name))
                return;

            if (invocation.ArgumentList.Arguments.Count == 0)
                return;

            ExpressionSyntax firstArgument = invocation.ArgumentList.Arguments[0].Expression;
            Optional<object?> constantValue = context.SemanticModel.GetConstantValue(firstArgument);

            if (!constantValue.HasValue || constantValue.Value is not string promptName)
                return;

            // Build available prompt names from AdditionalFiles by emulating PromptManager loading rules
            ImmutableArray<AdditionalText> additionalFiles = context.Options.AdditionalFiles;
            HashSet<string> availablePromptNames = BuildAvailablePromptNames(additionalFiles);

            if (!availablePromptNames.Contains(promptName))
            {
                Diagnostic diagnostic = Diagnostic.Create(Rule, firstArgument.GetLocation(), promptName);
                context.ReportDiagnostic(diagnostic);
            }
        }

        /// <summary>
        /// Builds the set of available prompt names from <see cref="AdditionalText"/> files by:
        /// - filtering to <c>.pmt</c> files
        /// - parsing the <c>information_name</c> value
        /// - prefixing with the lower-cased subdirectory path under <c>RConfigs/Prompts/</c> (with forward slashes and trailing slash)
        /// </summary>
        /// <param name="additionalFiles">The collection of additional files configured for the compilation.</param>
        /// <returns>A case-sensitive set of fully-resolved prompt names.</returns>
        private static HashSet<string> BuildAvailablePromptNames(ImmutableArray<AdditionalText> additionalFiles)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);

            foreach (AdditionalText file in additionalFiles)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = file.GetText() is { } sourceText ? sourceText.ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                string? infoName = TryParseInformationName(text);
                if (string.IsNullOrEmpty(infoName))
                    continue;

                string folderPrefix = ExtractPromptFolderPrefix(path);
                string fullName = folderPrefix + infoName;
                names.Add(fullName);
            }

            return names;
        }

        /// <summary>
        /// Extracts the folder prefix under an <c>RConfigs/Prompts/</c> segment from a full path.
        /// The returned value uses forward slashes and includes a trailing slash when not empty, lower-cased.
        /// If the segment is not present or there are no subdirectories, returns an empty string.
        /// </summary>
        /// <param name="fullPath">The full OS path to the file.</param>
        /// <returns>The normalized folder prefix.</returns>
        private static string ExtractPromptFolderPrefix(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return string.Empty;

            string normalized = fullPath.Replace('\\', '/');

            // Find "RConfigs/Prompts/" (case-insensitive)
            int idx = normalized.IndexOf("RConfigs/Prompts/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return string.Empty;

            int start = idx + "RConfigs/Prompts/".Length;
            // Slice after base to end, then remove file name
            string afterBase = normalized.Substring(start);
            int lastSlash = afterBase.LastIndexOf('/') ;
            if (lastSlash <= 0)
                return string.Empty;

            string directories = afterBase.Substring(0, lastSlash + 1); // keep trailing '/'
            return directories.ToLowerInvariant();
        }

        /// <summary>
        /// Attempts to parse the <c>information_name</c> value from a .pmt file content.
        /// Supports simple key-value with ':' or '=' separators, optionally quoted.
        /// </summary>
        /// <param name="content">The file content.</param>
        /// <returns>The trimmed value if found; otherwise null.</returns>
        private static string? TryParseInformationName(string content)
        {
            // Regex: start of line, key, separator, capture the value until EOL
            Regex rx = new Regex(@"^\s*information_name\s*[:=]\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            Match m = rx.Match(content);
            string? value;
            if (m.Success)
            {
                value = m.Groups[1].Value.Trim();
            }
            else
            {
                // Support RConfig section syntax: [[information]] then a line like: name = <value>
                Regex sectionRx = new Regex(@"\[\[\s*information\s*\]\](?<body>.*?)(?:\n\s*\[\[|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                Match s = sectionRx.Match(content);
                if (!s.Success)
                    return null;

                string body = s.Groups["body"].Value;
                Regex nameRx = new Regex(@"^\s*name\s*[:=]\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                Match nm = nameRx.Match(body);
                if (!nm.Success)
                    return null;
                value = nm.Groups[1].Value.Trim();
            }

            // Strip surrounding quotes if present
            if ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) ||
                (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal)))
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}
