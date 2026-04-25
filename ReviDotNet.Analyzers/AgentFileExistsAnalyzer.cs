// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// REVI006: Verifies that agent names passed to Agent.Run / Agent.ToString exist in .agent AdditionalFiles.
    /// Mirrors PromptFileExistsAnalyzer (REVI001) with .agent extension and RConfigs/Agents/ path.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AgentFileExistsAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "REVI006";

        private static readonly LocalizableString Title = "Missing Agent";
        private static readonly LocalizableString MessageFormat = "Agent '{0}' not found in AdditionalFiles (RConfigs/Agents)";
        private static readonly LocalizableString Description = "All agent names used in Agent methods must exist in AdditionalFiles and follow the same name resolution rules (folder prefix + information_name).";
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

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return;

            ISymbol? resolved = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            if (resolved is not IMethodSymbol symbol)
                return;

            if (symbol.ContainingType.Name != "Agent")
                return;

            string? nsName = symbol.ContainingType.ContainingNamespace?.Name;
            if (nsName != "Revi" && nsName != "ReviDotNet")
                return;

            string[] targetMethods = { "Run", "ToString", "FindAgent" };
            if (!System.Linq.Enumerable.Contains(targetMethods, symbol.Name))
                return;

            if (invocation.ArgumentList.Arguments.Count == 0)
                return;

            ExpressionSyntax firstArg = invocation.ArgumentList.Arguments[0].Expression;
            Optional<object?> constantValue = context.SemanticModel.GetConstantValue(firstArg);

            if (!constantValue.HasValue || constantValue.Value is not string agentName)
                return;

            HashSet<string> available = BuildAvailableAgentNames(context.Options.AdditionalFiles);

            if (!available.Contains(agentName))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, firstArg.GetLocation(), agentName));
            }
        }

        private static HashSet<string> BuildAvailableAgentNames(ImmutableArray<AdditionalText> additionalFiles)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);

            foreach (AdditionalText file in additionalFiles)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".agent", StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = file.GetText() is { } src ? src.ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                string? infoName = TryParseInformationName(text);
                if (string.IsNullOrEmpty(infoName))
                    continue;

                string prefix = ExtractAgentFolderPrefix(path);
                names.Add(prefix + infoName);
            }

            return names;
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

        private static string? TryParseInformationName(string content)
        {
            // Try flat key first: information_name = value
            Regex rx = new Regex(@"^\s*information_name\s*[:=]\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            Match m = rx.Match(content);
            string? value;

            if (m.Success)
            {
                value = m.Groups[1].Value.Trim();
            }
            else
            {
                // Try [[information]] section with name = value
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

            // Strip surrounding quotes
            if ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) ||
                (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal)))
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}
