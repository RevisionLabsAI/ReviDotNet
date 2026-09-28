using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>Decision call names agree with RConfig metadata, never identifiers inside the YAML body.</summary>
public sealed class DecisionFileAnalyzerTests
{
    private const string Source = "namespace Revi { public interface IDecisionService { void EvaluateAsync(string promptName); } } class Caller { void Go(Revi.IDecisionService service) { service.EvaluateAsync(\"route\"); } }";
    [Fact]
    public Task DeclaredNameMatchesThroughOrganizationalFolders() => AnalyzerTestHelper.RunAsync<DecisionFileAnalyzer>(Source,
        [("RConfigs/Decisions/nested/route.decision", "[[information]]\nname = route\nversion = 1\n[[settings]]\nmodel = decision-default\n[[_decision]]\nquestions: {}\n")]);

    [Theory]
    [InlineData("name: route\nversion: 1\nmodel: decision-default\nquestions: {}")]
    [InlineData("[[information]]\nname = route\nname = other\nversion = 1\n[[settings]]\nmodel = m\n[[_decision]]\nquestions: {}")]
    [InlineData("[[_decision]]\nname: route\nversion: 1\nmodel: m")]
    public Task RejectsLegacyYamlAndAmbiguousOrMissingMetadata(string text) => AnalyzerTestHelper.RunAsync<DecisionFileAnalyzer>("class C { }",
        [("RConfigs/Decisions/route.decision", text)],
        new DiagnosticResult(DecisionFileAnalyzer.HeaderId, DiagnosticSeverity.Error).WithArguments("RConfigs/Decisions/route.decision"));
    [Fact]
    public Task MissingDecisionReportsAtCallSite() => AnalyzerTestHelper.RunAsync<DecisionFileAnalyzer>(Source.Replace("\"route\"", "{|#0:\"route\"|}"), [],
        new DiagnosticResult(DecisionFileAnalyzer.MissingId, DiagnosticSeverity.Error).WithLocation(0).WithArguments("route"));
    [Fact]
    public Task UnrelatedMethodIsNotTreatedAsDecisionService() => AnalyzerTestHelper.RunAsync<DecisionFileAnalyzer>(Source.Replace("namespace Revi", "namespace Other").Replace("Revi.IDecisionService", "Other.IDecisionService"), []);
}
