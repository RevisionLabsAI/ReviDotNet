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
    /// REVI008: Reports when Agent.Run or Agent.ToString is called with a non-constant agent name.
    /// Mirrors NonConstantPromptNameAnalyzer (REVI002) for the Agent API.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NonConstantAgentNameAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "REVI008";

        private static readonly LocalizableString Title = "Non-constant agent name";
        private static readonly LocalizableString MessageFormat = "The agent name passed to '{0}' should be a constant string (e.g., a literal or nameof)";
        private static readonly LocalizableString Description = "To enable agent existence validation, pass a constant agent name to Agent APIs.";
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

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

            if (methodSymbol.ContainingType.Name != "Agent")
                return;

            string? nsName = methodSymbol.ContainingType.ContainingNamespace?.Name;
            if (nsName != "Revi" && nsName != "ReviDotNet")
                return;

            string[] targetMethods = { "Run", "ToString", "FindAgent" };
            if (!targetMethods.Contains(methodSymbol.Name))
                return;

            if (invocation.ArgumentList.Arguments.Count == 0)
                return;

            ExpressionSyntax firstArg = invocation.ArgumentList.Arguments[0].Expression;
            Optional<object?> constVal = context.SemanticModel.GetConstantValue(firstArg, context.CancellationToken);

            if (constVal.HasValue && constVal.Value is string)
                return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, firstArg.GetLocation(), methodSymbol.Name));
        }
    }
}
