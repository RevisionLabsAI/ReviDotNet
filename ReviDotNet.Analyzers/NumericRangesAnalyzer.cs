using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// REVI021: Validates numeric ranges for common inference parameters like temperature, top_p, penalties, and inactivity timeouts.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NumericRangesAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "REVI021";

        private static readonly LocalizableString Title = "Numeric parameter out of recommended bounds";
        private static readonly LocalizableString Message = "Parameter '{0}' with value {1} is outside recommended bounds {2}";
        private static readonly LocalizableString Description = "Validate values like temperature, top_p, presence/frequency penalties, and inactivity timeouts to avoid accidental misuse.";
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
            if (resolved is not IMethodSymbol method)
            {
                return;
            }

            INamedTypeSymbol type = method.ContainingType;
            string ns = type.ContainingNamespace?.Name ?? string.Empty;
            if (type.Name != "Infer" || (ns != "Revi" && ns != "ReviDotNet"))
            {
                return;
            }

            foreach (ArgumentSyntax arg in invocation.ArgumentList.Arguments)
            {
                string? name = arg.NameColon?.Name.Identifier.ValueText;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!TryGetConstantDouble(context.SemanticModel, arg.Expression, out double value))
                {
                    continue;
                }

                switch (name)
                {
                    case "temperature":
                        // [0.0, 2.0]
                        if (value < 0.0)
                        {
                            Report(arg, name, value, "[0.0, 2.0]", DiagnosticSeverity.Error, context);
                        }
                        else if (value > 2.0)
                        {
                            Report(arg, name, value, "[0.0, 2.0]", DiagnosticSeverity.Warning, context);
                        }
                        break;
                    case "top_p":
                        // (0.0, 1.0]
                        if (value <= 0.0)
                        {
                            Report(arg, name, value, "(0.0, 1.0]", DiagnosticSeverity.Error, context);
                        }
                        else if (value > 1.0)
                        {
                            Report(arg, name, value, "(0.0, 1.0]", DiagnosticSeverity.Warning, context);
                        }
                        break;
                    case "presence_penalty":
                    case "frequency_penalty":
                        // [-2.0, 2.0]
                        if (value < -2.0 || value > 2.0)
                        {
                            Report(arg, name, value, "[-2.0, 2.0]", DiagnosticSeverity.Warning, context);
                        }
                        break;
                    case "inactivity_timeout_seconds":
                        // [0, 3600]
                        if (value < 0.0)
                        {
                            Report(arg, name, value, "[0, 3600]", DiagnosticSeverity.Error, context);
                        }
                        else if (value > 3600.0)
                        {
                            Report(arg, name, value, "[0, 3600]", DiagnosticSeverity.Warning, context);
                        }
                        break;
                }
            }
        }

        private static bool TryGetConstantDouble(SemanticModel model, ExpressionSyntax expr, out double value)
        {
            value = 0;
            Optional<object?> constant = model.GetConstantValue(expr);
            if (!constant.HasValue || constant.Value == null)
            {
                return false;
            }
            object v = constant.Value;
            switch (v)
            {
                case double d: value = d; return true;
                case float f: value = f; return true;
                case int i: value = i; return true;
                case long l: value = l; return true;
                case decimal m: value = (double)m; return true;
                default: return false;
            }
        }

        private static void Report(ArgumentSyntax arg, string param, double value, string bounds, DiagnosticSeverity severity, SyntaxNodeAnalysisContext context)
        {
            DiagnosticDescriptor descriptor = severity == DiagnosticSeverity.Error
                ? new DiagnosticDescriptor(DiagnosticId, Title, Message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description)
                : Rule;
            Location loc = arg.GetLocation();
            Diagnostic d = Diagnostic.Create(descriptor, loc, param, value, bounds);
            context.ReportDiagnostic(d);
        }
    }
}
