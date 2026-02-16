using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Analyzes calls to Infer methods to ensure the specified prompt file exists.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class PromptFileExistsAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "REVI001";

        private static readonly LocalizableString Title = "Missing Prompt File";
        private static readonly LocalizableString MessageFormat = "Prompt file '{0}.pmt' not found in AdditionalFiles";
        private static readonly LocalizableString Description = "All prompts used in Infer methods must have a corresponding .pmt file.";
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
            var invocation = (InvocationExpressionSyntax)context.Node;
            
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return;

            // Check if the class is "Infer" (or "Revi.Infer")
            var symbol = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
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

            var firstArgument = invocation.ArgumentList.Arguments[0].Expression;
            var constantValue = context.SemanticModel.GetConstantValue(firstArgument);

            if (!constantValue.HasValue || constantValue.Value is not string promptName)
                return;

            // Check if promptName.pmt exists in AdditionalFiles
            var additionalFiles = context.Options.AdditionalFiles;
            string fileName = $"{promptName}.pmt";
            
            bool fileExists = additionalFiles.Any(file => 
                Path.GetFileName(file.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (!fileExists)
            {
                var diagnostic = Diagnostic.Create(Rule, firstArgument.GetLocation(), promptName);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }
}
