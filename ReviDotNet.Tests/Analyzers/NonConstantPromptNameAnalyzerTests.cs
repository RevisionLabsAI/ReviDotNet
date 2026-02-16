using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using ReviDotNet.Tests.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>
/// Tests for REVI002: NonConstantPromptNameAnalyzer.
/// </summary>
public sealed class NonConstantPromptNameAnalyzerTests
{
    [Fact]
    public async Task NoDiagnostic_WithLiteral()
    {
        string src = "using Revi; class C { void M(){ _ = Infer.ToString(\"ok\"); } }";
        await AnalyzerTestHelper.RunAsync<NonConstantPromptNameAnalyzer>(src, []);
    }

    [Fact]
    public async Task NoDiagnostic_WithConst()
    {
        string src = "using Revi; class C { const string P = \"ok\"; void M(){ _ = Infer.ToString(P); } }";
        await AnalyzerTestHelper.RunAsync<NonConstantPromptNameAnalyzer>(src, []);
    }

    [Fact]
    public async Task Warning_WithVariable()
    {
        string src = "using Revi; class C { void M(){ string p = \"x\"; _ = Infer.ToString(/*0*/p/*0*/); } }";
        DiagnosticResult expected = new DiagnosticResult(NonConstantPromptNameAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan("/0/Test1.cs", 1, 73, 1, 74)
            .WithArguments("ToString");
        await AnalyzerTestHelper.RunAsync<NonConstantPromptNameAnalyzer>(src, [], expected);
    }
}
