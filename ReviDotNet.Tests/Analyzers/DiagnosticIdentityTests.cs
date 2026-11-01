// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using ReviDotNet.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>Unrelated analyzers and different default severities must remain independently configurable.</summary>
public sealed class DiagnosticIdentityTests
{
    /// <summary>Runtime forwarding exceptions must not disable the warning for application call sites.</summary>
    [Fact]
    public async Task ApplicationAgentNamesStillRequireConstantsForStaticValidation()
    {
        const string source = "using Revi; class C { void M(IAgentService agent, string name) { _ = agent.Run({|#0:name|}); } }";
        DiagnosticResult expected = DiagnosticResult.CompilerWarning(NonConstantAgentNameAnalyzer.DiagnosticId)
            .WithLocation(0).WithArguments("Run");
        await AnalyzerTestHelper.RunAsync<NonConstantAgentNameAnalyzer>(source, [], expected);
    }
    /// <summary>A diagnostic ID belongs to one analyzer and one supported default severity.</summary>
    [Fact]
    public void DiagnosticIdsAreUnambiguousAcrossTheAnalyzerAssembly()
    {
        var descriptors = typeof(PromptMetadataSchemaAnalyzer).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            .Select(type => (Type: type, Analyzer: (DiagnosticAnalyzer)Activator.CreateInstance(type)!))
            .SelectMany(item => item.Analyzer.SupportedDiagnostics.Select(descriptor => (item.Type, Descriptor: descriptor)))
            .ToArray();
        foreach (var group in descriptors.GroupBy(item => item.Descriptor.Id))
        {
            group.Select(item => item.Type).Distinct().Should().ContainSingle($"{group.Key} must not control unrelated analyzers");
            group.Select(item => item.Descriptor.DefaultSeverity).Distinct().Should().ContainSingle($"{group.Key} must have one configurable default severity");
        }
        new AgentFileExistsAnalyzer().SupportedDiagnostics.Should().OnlyContain(rule => rule.Id == "REVI006");
        new PromptMetadataSchemaAnalyzer().SupportedDiagnostics.Should().NotContain(rule => rule.Id == "REVI006");
        new PromptMetadataSchemaAnalyzer().SupportedDiagnostics.Where(rule => rule.DefaultSeverity == DiagnosticSeverity.Error)
            .Should().OnlyContain(rule => rule.Id == "REVI030");
        new PromptMetadataSchemaAnalyzer().SupportedDiagnostics.Where(rule => rule.DefaultSeverity == DiagnosticSeverity.Warning)
            .Should().OnlyContain(rule => rule.Id == "REVI031");
    }
}
