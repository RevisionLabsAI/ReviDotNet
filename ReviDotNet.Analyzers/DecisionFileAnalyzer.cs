using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReviDotNet.Analyzers;

/// <summary>Checks decision-file identifiers and named decision service calls. Full rubric validation runs at load time.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DecisionFileAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Malformed decision file header.</summary>
    public const string HeaderId = "REVI060";
    /// <summary>Unknown decision prompt reference.</summary>
    public const string MissingId = "REVI061";
    private static readonly DiagnosticDescriptor Header = new(HeaderId, "Invalid decision header", "Decision file '{0}' requires valid [[information]], [[settings]], and [[_decision]] RConfig sections", "Configuration", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Missing = new(MissingId, "Missing decision prompt", "Decision prompt '{0}' is not present in AdditionalFiles", "Usage", DiagnosticSeverity.Error, true);
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Header, Missing);
    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            List<string> invalid = new();
            foreach (AdditionalText file in start.Options.AdditionalFiles.Where(f => f.Path.EndsWith(".decision", StringComparison.OrdinalIgnoreCase)))
            {
                string text = file.GetText(start.CancellationToken)?.ToString() ?? "";
                try { names.Add(Revi.ConfigName.Resolve(Revi.DecisionFileHeader.ReadMetadata(text)["information_name"])); }
                catch (FormatException) { invalid.Add(file.Path); }
            }
            start.RegisterCompilationEndAction(end => { foreach (string path in invalid) end.ReportDiagnostic(Diagnostic.Create(Header, Location.None, path)); });
            start.RegisterSyntaxNodeAction(node =>
            {
                InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)node.Node;
                if (node.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method || method.ContainingNamespace.ToDisplayString() != "Revi" ||
                    method.ContainingType.Name is not ("IDecisionService" or "DecisionService") || method.Parameters.Length == 0 || method.Parameters[0].Name != "promptName") return;
                ArgumentSyntax? argument = invocation.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "promptName")
                    ?? invocation.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon is null);
                if (argument is null) return;
                Optional<object?> constant = node.SemanticModel.GetConstantValue(argument.Expression);
                if (constant.HasValue && constant.Value is string name && !names.Contains(name)) node.ReportDiagnostic(Diagnostic.Create(Missing, argument.Expression.GetLocation(), name));
            }, SyntaxKind.InvocationExpression);
        });
    }
}
