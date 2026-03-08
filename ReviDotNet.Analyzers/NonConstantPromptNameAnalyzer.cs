// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Reports when an Infer API is called with a non-constant prompt name.
    /// Acceptable values are string literals and compile-time constants (including nameof expressions).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NonConstantPromptNameAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for non-constant prompt names.
        /// </summary>
        public const string DiagnosticId = "REVI002";

        private static readonly LocalizableString Title = "Non-constant prompt name";
        private static readonly LocalizableString MessageFormat = "The prompt name passed to '{0}' should be a constant string (e.g., a literal or nameof)";
        private static readonly LocalizableString Description = "To enable prompt existence and placeholder validation, pass a constant prompt name to Infer APIs.";
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <summary>
        /// Initialize the analyzer with a syntax node action for invocation expressions.
        /// </summary>
        /// <param name="context">The analysis context.</param>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        /// <summary>
        /// Analyze method invocations for calls to Revi.Infer.* where the first parameter (prompt name) is non-constant.
        /// </summary>
        /// <param name="context">The syntax node analysis context.</param>
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

            if (invocation.ArgumentList.Arguments.Count == 0)
                return;

            ExpressionSyntax firstArg = invocation.ArgumentList.Arguments[0].Expression;

            // If we can resolve a constant string value, it's fine (supports literals and nameof/consts).
            Optional<object?> constVal = context.SemanticModel.GetConstantValue(firstArg, context.CancellationToken);
            if (constVal.HasValue && constVal.Value is string)
                return;

            Diagnostic diagnostic = Diagnostic.Create(Rule, firstArg.GetLocation(), methodSymbol.Name);
            context.ReportDiagnostic(diagnostic);
        }
    }
}
