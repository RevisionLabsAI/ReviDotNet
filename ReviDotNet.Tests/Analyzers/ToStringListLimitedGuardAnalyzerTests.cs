using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>
/// Tests for REVI022: ToStringListLimited missing both maxLines and evaluator.
/// </summary>
public sealed class ToStringListLimitedGuardAnalyzerTests
{
    [Fact]
    public async Task Reports_When_Both_Missing()
    {
        string src = @"using Revi; class C { void M(){ var s = Infer.ToStringListLimited(""P""); } }";
        DiagnosticResult expected = new DiagnosticResult(ToStringListLimitedGuardAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan("/0/Test1.cs", 1, 41, 1, 71);
        await AnalyzerTestHelper.RunAsync<ToStringListLimitedGuardAnalyzer>(src, [], expected);
    }

    [Fact]
    public async Task No_Report_When_MaxLines_Provided()
    {
        string src = @"using Revi; class C { void M(){ var s = Infer.ToStringListLimited(""P"", maxLines: 5); } }";
        await AnalyzerTestHelper.RunAsync<ToStringListLimitedGuardAnalyzer>(src, []);
    }

    [Fact]
    public async Task No_Report_When_Evaluator_Provided()
    {
        string src = @"using Revi; class C { void M(){ var s = Infer.ToStringListLimited(""P"", evaluator: s => true); } }";
        await AnalyzerTestHelper.RunAsync<ToStringListLimitedGuardAnalyzer>(src, []);
    }

    [Fact]
    public async Task Reports_When_Both_Explicit_Null()
    {
        string src = @"using Revi; class C { void M(){ var s = Infer.ToStringListLimited(""P"", null, null); } }";
        DiagnosticResult expected = new DiagnosticResult(ToStringListLimitedGuardAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan("/0/Test1.cs", 1, 41, 1, 83);
        await AnalyzerTestHelper.RunAsync<ToStringListLimitedGuardAnalyzer>(src, [], expected);
    }
}
