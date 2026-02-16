using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>
/// Tests for REVI021: Numeric ranges validation.
/// </summary>
public sealed class NumericRangesAnalyzerTests
{
    [Fact]
    public async Task Warns_On_OutOfRange_And_Errors_On_InvalidDomain()
    {
        string src = @"using Revi; class C { void M(){
var a = Infer.ToString(""p"", temperature: 2.5);
var b = Infer.ToString(""p"", top_p: 0.0);
var c = Infer.ToString(""p"", presence_penalty: 3);
var d = Infer.ToString(""p"", inactivity_timeout_seconds: -5);
} }";

        DiagnosticResult d1 = new DiagnosticResult(NumericRangesAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan("/0/Test1.cs", 2, 29, 2, 45);
        DiagnosticResult d2 = new DiagnosticResult(NumericRangesAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan("/0/Test1.cs", 3, 29, 3, 39);
        DiagnosticResult d3 = new DiagnosticResult(NumericRangesAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan("/0/Test1.cs", 4, 29, 4, 48);
        DiagnosticResult d4 = new DiagnosticResult(NumericRangesAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan("/0/Test1.cs", 5, 29, 5, 59);

        await AnalyzerTestHelper.RunAsync<NumericRangesAnalyzer>(src, [], d1, d2, d3, d4);
    }

    [Fact]
    public async Task No_Warnings_On_ValidRanges()
    {
        string src = @"using Revi; class C { void M(){
var a = Infer.ToString(""p"", temperature: 1.0, top_p: 1.0, presence_penalty: 0, frequency_penalty: -1.5, inactivity_timeout_seconds: 30);
} }";
        await AnalyzerTestHelper.RunAsync<NumericRangesAnalyzer>(src, []);
    }
}
