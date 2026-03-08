// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// REVI020: Reports usage of <c>Infer.ToEnum&lt;T&gt;</c> when <c>T</c> is not an enum type.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ToEnumGenericTypeAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for non-enum generic argument in <c>ToEnum&lt;T&gt;</c>.
        /// </summary>
        public const string DiagnosticId = "REVI020";

        private static readonly LocalizableString Title = "ToEnum<T> requires an enum type";
        private static readonly LocalizableString Message = "Generic argument '{0}' is not an enum type";
        private static readonly LocalizableString Description = "Revi.Infer.ToEnum<T> must be used with an enum type parameter.";
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            Message,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Description);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

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
            {
                return;
            }

            ISymbol? symbol = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            if (symbol is not IMethodSymbol method)
            {
                return;
            }

            // Only interested in Revi.Infer.ToEnum
            INamedTypeSymbol containingType = method.ContainingType;
            string nsName = containingType.ContainingNamespace?.Name ?? string.Empty;
            if (containingType.Name != "Infer" || (nsName != "Revi" && nsName != "ReviDotNet"))
            {
                return;
            }
            if (method.Name != "ToEnum")
            {
                return;
            }

            // Resolve the constructed generic method and get T
            if (memberAccess.Name is GenericNameSyntax genericName && genericName.TypeArgumentList.Arguments.Count == 1)
            {
                ITypeSymbol? typeArg = context.SemanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
                if (typeArg == null)
                {
                    return;
                }

                if (typeArg.TypeKind != TypeKind.Enum)
                {
                    // Report on the method name (e.g., ToEnum<int>) to keep span stable for tests
                    Diagnostic diagnostic = Diagnostic.Create(Rule, memberAccess.Name.GetLocation(), typeArg.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }
}
