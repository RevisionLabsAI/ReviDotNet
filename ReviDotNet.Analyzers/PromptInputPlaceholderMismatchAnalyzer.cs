// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Validates that inputs passed to Infer APIs match the placeholders declared in the referenced .pmt prompt file.
    /// - Reports an Error when a required placeholder is missing in provided inputs.
    /// - Reports a Warning when an input is provided but there is no matching placeholder in the prompt.
    /// Only analyzes calls where the prompt name is a compile-time constant and where inputs are provided inline
    /// via a collection initializer or single Input construction.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PromptInputPlaceholderMismatchAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for placeholder mismatch.
        /// </summary>
        public const string DiagnosticId = "REVI003";

        private static readonly LocalizableString MissingTitle = "Missing required prompt input";
        private static readonly LocalizableString MissingMessage = "Prompt '{0}' requires input '{1}'";
        private static readonly LocalizableString MissingDescription = "All placeholders declared in the .pmt file must have corresponding Input entries.";

        private static readonly LocalizableString ExtraTitle = "Unused input provided";
        private static readonly LocalizableString ExtraMessage = "Input '{0}' is not used by prompt '{1}'";
        private static readonly LocalizableString ExtraDescription = "Inputs without matching placeholders in the prompt are likely mistakes and can be removed or renamed.";

        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor MissingRule = new DiagnosticDescriptor(
            DiagnosticId,
            MissingTitle,
            MissingMessage,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: MissingDescription);

        private static readonly DiagnosticDescriptor ExtraRule = new DiagnosticDescriptor(
            DiagnosticId,
            ExtraTitle,
            ExtraMessage,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: ExtraDescription);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(MissingRule, ExtraRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return;

            ISymbol? resolved = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            if (resolved is not IMethodSymbol methodSymbol)
                return;

            if (methodSymbol.ContainingType.Name != "Infer")
                return;
            string? nsName = methodSymbol.ContainingType.ContainingNamespace?.Name;
            if (nsName != "Revi" && nsName != "ReviDotNet")
                return;

            string[] targetMethods = { "ToObject", "ToEnum", "ToString", "ToStringList", "ToStringListLimited", "ToBool", "ToJObject", "Completion" };
            if (!targetMethods.Contains(methodSymbol.Name))
                return;

            // Need at least the prompt name argument
            if (invocation.ArgumentList.Arguments.Count == 0)
                return;

            ExpressionSyntax promptArg = invocation.ArgumentList.Arguments[0].Expression;
            Optional<object?> constVal = context.SemanticModel.GetConstantValue(promptArg, context.CancellationToken);
            if (!constVal.HasValue || constVal.Value is not string promptName)
                return; // Only analyze when we know the prompt name

            // Build placeholders for this prompt by scanning AdditionalFiles
            ImmutableArray<AdditionalText> additionalFiles = context.Options.AdditionalFiles;
            Dictionary<string, (HashSet<string> Placeholders, AdditionalText File)> promptMap = BuildPromptPlaceholders(additionalFiles);
            if (!promptMap.TryGetValue(promptName, out (HashSet<string> Placeholders, AdditionalText File) promptInfo))
                return; // Prompt not found; REVI001/exists analyzer will handle this

            HashSet<string> required = promptInfo.Placeholders;

            // Try to read provided inputs from the second argument if present
            HashSet<string> provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Location? extraLocation = null;
            if (invocation.ArgumentList.Arguments.Count >= 2)
            {
                ArgumentSyntax inputsArg = invocation.ArgumentList.Arguments[1];
                ExtractInputNames(context, inputsArg.Expression, provided, out extraLocation);
            }

            // If we couldn't resolve any inputs and required is empty, nothing to do
            if (required.Count == 0 && provided.Count == 0)
                return;

            // Report missing required placeholders
            foreach (string req in required)
            {
                bool satisfied = provided.Contains(req);
                if (!satisfied)
                {
                    Diagnostic d = Diagnostic.Create(MissingRule, promptArg.GetLocation(), promptName, req);
                    context.ReportDiagnostic(d);
                }
            }

            // Report extras (inputs with no corresponding placeholder) when we have any provided
            if (provided.Count > 0)
            {
                foreach (string got in provided)
                {
                    if (!required.Contains(got))
                    {
                        Location loc = extraLocation ?? promptArg.GetLocation();
                        Diagnostic d = Diagnostic.Create(ExtraRule, loc, got, promptName);
                        context.ReportDiagnostic(d);
                    }
                }
            }
        }

        /// <summary>
        /// Builds a map of prompt name to placeholder set by scanning AdditionalFiles (.pmt) using the same naming rules
        /// as <see cref="PromptFileExistsAnalyzer"/>.
        /// </summary>
        private static Dictionary<string, (HashSet<string> Placeholders, AdditionalText File)> BuildPromptPlaceholders(ImmutableArray<AdditionalText> additionalFiles)
        {
            Dictionary<string, (HashSet<string>, AdditionalText)> map = new Dictionary<string, (HashSet<string>, AdditionalText)>(StringComparer.Ordinal);
            foreach (AdditionalText file in additionalFiles)
            {
                string path = file.Path ?? string.Empty;
                if (!path.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = file.GetText() is { } st ? st.ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                string? infoName = TryParseInformationName(text);
                if (string.IsNullOrEmpty(infoName))
                    continue;

                string folderPrefix = ExtractPromptFolderPrefix(path);
                string fullName = folderPrefix + infoName;

                HashSet<string> placeholders = ParsePlaceholders(text);
                map[fullName] = (placeholders, file);
            }
            return map;
        }

        /// <summary>
        /// Extract labels from Input expressions provided inline.
        /// Supported forms:
        /// - new List&lt;Input&gt; { new Input("Label", ...), ... }
        /// - new[] { new Input("Label", ...), ... }
        /// - new Input("Label", ...)
        /// </summary>
        private static void ExtractInputNames(SyntaxNodeAnalysisContext context, ExpressionSyntax expr, HashSet<string> sink, out Location? anyInputLocation)
        {
            anyInputLocation = null;

            switch (expr)
            {
                case ObjectCreationExpressionSyntax o when o.Initializer != null:
                    foreach (ExpressionSyntax initExpr in o.Initializer.Expressions)
                    {
                        if (TryGetInputLabel(context, initExpr, out string? label, out Location? loc))
                        {
                            anyInputLocation ??= loc;
                            AddIdentifierizedName(sink, label!);
                        }
                    }
                    break;
                case ArrayCreationExpressionSyntax a when a.Initializer != null:
                    foreach (ExpressionSyntax initExpr in a.Initializer.Expressions)
                    {
                        if (TryGetInputLabel(context, initExpr, out string? label, out Location? loc))
                        {
                            anyInputLocation ??= loc;
                            AddIdentifierizedName(sink, label!);
                        }
                    }
                    break;
                default:
                    // Single inline new Input("Label", ...)
                    if (TryGetInputLabel(context, expr, out string? singleLabel, out Location? singleLoc))
                    {
                        anyInputLocation = singleLoc;
                        AddIdentifierizedName(sink, singleLabel!);
                    }
                    break;
            }
        }

        private static bool TryGetInputLabel(SyntaxNodeAnalysisContext context, ExpressionSyntax expr, out string? label, out Location? location)
        {
            label = null;
            location = null;

            if (expr is not ObjectCreationExpressionSyntax o)
                return false;

            ITypeSymbol? type = context.SemanticModel.GetTypeInfo(o, context.CancellationToken).Type;
            if (type == null || type.Name != "Input")
                return false;

            if (o.ArgumentList == null || o.ArgumentList.Arguments.Count == 0)
                return false;

            ExpressionSyntax first = o.ArgumentList.Arguments[0].Expression;
            Optional<object?> constVal = context.SemanticModel.GetConstantValue(first, context.CancellationToken);
            if (constVal.HasValue && constVal.Value is string s)
            {
                label = s;
                location = first.GetLocation();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Convert a human label to an identifier-like name similar to Util.Identifierize: lowercase and replace non-alphanumerics with '-'.
        /// </summary>
        private static void AddIdentifierizedName(HashSet<string> set, string label)
        {
            string n1 = (label ?? string.Empty).Trim();
            string lowered = n1.ToLowerInvariant();
            string id = Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
            set.Add(id);
            set.Add(lowered); // be lenient: also accept raw lowercase label
        }

        /// <summary>
        /// Parse all ${name} placeholders from a .pmt file content.
        /// Returns a case-insensitive set of identifierized names.
        /// </summary>
        private static HashSet<string> ParsePlaceholders(string content)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Regex rx = new Regex(@"\$\{\s*([a-zA-Z0-9 _\-\.]+?)\s*\}", RegexOptions.Multiline);
            foreach (Match m in rx.Matches(content))
            {
                if (!m.Success)
                    continue;
                string name = m.Groups[1].Value;
                string lowered = name.Trim().ToLowerInvariant();
                string id = Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
                if (!string.IsNullOrEmpty(id))
                {
                    result.Add(id);
                    result.Add(lowered);
                }
            }
            return result;
        }

        /// <summary>
        /// Extracts the folder prefix under an RConfigs/Prompts/ segment from a full path (forward slashes, trailing '/', lowercased).
        /// </summary>
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
        /// Attempts to parse the information_name value from a .pmt file content.
        /// Mirrors the logic from PromptFileExistsAnalyzer.
        /// </summary>
        private static string? TryParseInformationName(string content)
        {
            Regex rx = new Regex(@"^\s*information_name\s*[:=]\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            Match m = rx.Match(content);
            string? value;
            if (m.Success)
            {
                value = m.Groups[1].Value.Trim();
            }
            else
            {
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

            if ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) ||
                (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal)))
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}
