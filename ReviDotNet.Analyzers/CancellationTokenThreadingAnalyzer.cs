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
    /// REVI026: Methods that accept a CancellationToken should thread it into Infer.* calls
    /// instead of passing default/None or omitting the token when an overload supports it.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class CancellationTokenThreadingAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for missing/incorrect CancellationToken threading.
        /// </summary>
        public const string DiagnosticId = "REVI026";

        private static readonly LocalizableString Title = "Pass through CancellationToken to Infer calls";
        private static readonly LocalizableString Message = "Method accepts a CancellationToken but does not pass it to this Infer call";
        private static readonly LocalizableString Description = "When a method has a CancellationToken parameter, it should pass that token to downstream Infer APIs that support a token parameter.";
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
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            // Get containing method and ensure it has a CancellationToken parameter
            IMethodSymbol? containingMethod = context.ContainingSymbol as IMethodSymbol;
            if (containingMethod == null)
            {
                return;
            }

            IParameterSymbol? tokenParam = containingMethod.Parameters.FirstOrDefault(p => p.Type.Name == "CancellationToken" && p.Type.ContainingNamespace.ToDisplayString() == "System.Threading");
            if (tokenParam == null)
            {
                return;
            }

            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return;
            }

            ISymbol? resolved = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            if (resolved is not IMethodSymbol target)
            {
                return;
            }

            INamedTypeSymbol type = target.ContainingType;
            string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (type.Name != "Infer" || (ns != "Revi" && ns != "ReviDotNet"))
            {
                return;
            }

            // If target has a CancellationToken parameter
            int tokenParamIndex = -1;
            for (int i = 0; i < target.Parameters.Length; i++)
            {
                IParameterSymbol p = target.Parameters[i];
                if (p.Type.Name == "CancellationToken" && p.Type.ContainingNamespace.ToDisplayString() == "System.Threading")
                {
                    tokenParamIndex = i;
                    break;
                }
            }

            if (tokenParamIndex < 0)
            {
                return;
            }

            // Check if argument is provided for that parameter
            SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
            ArgumentSyntax? suppliedArg = null;
            for (int i = 0; i < args.Count; i++)
            {
                ArgumentSyntax arg = args[i];
                if (arg.NameColon != null)
                {
                    if (arg.NameColon.Name.Identifier.ValueText == target.Parameters[tokenParamIndex].Name)
                    {
                        suppliedArg = arg;
                        break;
                    }
                }
                else if (i == tokenParamIndex)
                {
                    suppliedArg = arg;
                    break;
                }
            }

            if (suppliedArg == null)
            {
                // Omitted: report
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
                return;
            }

            // Provided but ensure it's not default/None
            ExpressionSyntax expr = suppliedArg.Expression;
            if (expr.IsKind(SyntaxKind.DefaultLiteralExpression))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, expr.GetLocation()));
                return;
            }
            if (expr is MemberAccessExpressionSyntax mae && mae.Name.Identifier.ValueText == "None" &&
                context.SemanticModel.GetTypeInfo(mae.Expression).Type?.ToDisplayString() == "System.Threading.CancellationToken")
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, expr.GetLocation()));
                return;
            }

            // If expression is an identifier, verify it binds to the enclosing token parameter
            if (expr is IdentifierNameSyntax id)
            {
                ISymbol? sym = context.SemanticModel.GetSymbolInfo(id).Symbol;
                if (!SymbolEqualityComparer.Default.Equals(sym, tokenParam))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, expr.GetLocation()));
                }
            }
        }
    }
}
