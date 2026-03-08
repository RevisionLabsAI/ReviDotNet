// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using ReviDotNet.Tests.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>
/// Tests for REVI004: DuplicatePromptNameAnalyzer.
/// </summary>
public sealed class DuplicatePromptNameAnalyzerTests
{
    [Fact]
    public async Task Warning_WhenDuplicateLogicalNames()
    {
        string src = "class C { }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/alpha/a1.pmt", "[[information]]\nname = x\n"),
            ("C:/proj/RConfigs/Prompts/alpha/a2.pmt", "[[information]]\nname = x\n")
        ];

        DiagnosticResult d1 = new DiagnosticResult(DuplicatePromptNameAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan("C:/proj/RConfigs/Prompts/alpha/a1.pmt", 1, 1, 1, 1)
            .WithArguments("alpha/x");
        DiagnosticResult d2 = new DiagnosticResult(DuplicatePromptNameAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan("C:/proj/RConfigs/Prompts/alpha/a2.pmt", 1, 1, 1, 1)
            .WithArguments("alpha/x");

        await AnalyzerTestHelper.RunAsync<DuplicatePromptNameAnalyzer>(src, files, d1, d2);
    }

    [Fact]
    public async Task NoDiagnostic_WhenNamesDiffer()
    {
        string src = "class C { }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/alpha/a1.pmt", "[[information]]\nname = x\n"),
            ("C:/proj/RConfigs/Prompts/alpha/a2.pmt", "[[information]]\nname = y\n")
        ];

        await AnalyzerTestHelper.RunAsync<DuplicatePromptNameAnalyzer>(src, files);
    }
}
