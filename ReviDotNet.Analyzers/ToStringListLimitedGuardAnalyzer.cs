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
    /// REVI022: Warn when calling Infer.ToStringListLimited without providing either maxLines or evaluator.
    /// Both parameters omitted or explicitly null often indicates accidental misuse.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ToStringListLimitedGuardAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for missing limiting condition in ToStringListLimited.
        /// </summary>
        public const string DiagnosticId = "REVI022";

        private static readonly LocalizableString Title = "ToStringListLimited should specify maxLines or evaluator";
        private static readonly LocalizableString Message = "Provide either 'maxLines' or 'evaluator' to limit the list size";
        private static readonly LocalizableString Description = "Calling Revi.Infer.ToStringListLimited without any limiting argument can be accidental; supply either maxLines or an evaluator.";
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
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return;
            }

            ISymbol? resolved = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
            if (resolved is not IMethodSymbol symbol)
            {
                return;
            }

            INamedTypeSymbol containingType = symbol.ContainingType;
            string nsName = containingType.ContainingNamespace?.Name ?? string.Empty;
            if (containingType.Name != "Infer" || (nsName != "Revi" && nsName != "ReviDotNet"))
            {
                return;
            }
            if (symbol.Name != "ToStringListLimited")
            {
                return;
            }

            // Arguments after promptName: maxLines?, evaluator?
            SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
            bool hasMax = false;
            bool hasEval = false;

            for (int i = 0; i < args.Count; i++)
            {
                ArgumentSyntax arg = args[i];
                string? name = arg.NameColon?.Name.Identifier.ValueText;
                // positional index 1 => maxLines, 2 => evaluator in our stub; also allow named args
                if (name == "maxLines" || (name == null && i == 1))
                {
                    hasMax = !IsNullLiteral(arg.Expression, context.SemanticModel);
                }
                else if (name == "evaluator" || (name == null && i == 2))
                {
                    hasEval = !IsNullLiteral(arg.Expression, context.SemanticModel);
                }
            }

            if (!hasMax && !hasEval)
            {
                Location loc = invocation.GetLocation();
                context.ReportDiagnostic(Diagnostic.Create(Rule, loc));
            }
        }

        private static bool IsNullLiteral(ExpressionSyntax expr, SemanticModel model)
        {
            if (expr.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return true;
            }
            Optional<object?> constant = model.GetConstantValue(expr);
            return constant.HasValue && constant.Value is null;
        }
    }
}
