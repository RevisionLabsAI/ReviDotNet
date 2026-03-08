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
/// Tests for REVI003: PromptInputPlaceholderMismatchAnalyzer.
/// </summary>
public sealed class PromptInputPlaceholderMismatchAnalyzerTests
{
    [Fact]
    public async Task Error_WhenRequiredPlaceholderMissing()
    {
        string src = "using Revi; class C { void M(){ _ = Infer.ToString(\"p\", new System.Collections.Generic.List<Input>{ new Input(\"city\", \"LA\") }); } }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/p.pmt", "[[information]]\nname = p\n\n[_instruction]\nHello ${user} in ${city}!")
        ];

        DiagnosticResult expected = new DiagnosticResult(PromptInputPlaceholderMismatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithSpan("/0/Test1.cs", 1, 52, 1, 55)
            .WithArguments("p", "user");

        await AnalyzerTestHelper.RunAsync<PromptInputPlaceholderMismatchAnalyzer>(src, files, expected);
    }

    [Fact]
    public async Task Warning_WhenExtraInputProvided()
    {
        string src = "using Revi; using System.Collections.Generic; class C { void M(){ _ = Infer.ToString(\"p\", new List<Input>{ new Input(\"user\", \"A\"), new Input(\"extra\", \"B\") }); } }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/p.pmt", "[[information]]\nname = p\n\n[_instruction]\nHello ${user}!")
        ];

        DiagnosticResult expected = new DiagnosticResult(PromptInputPlaceholderMismatchAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan("/0/Test1.cs", 1, 118, 1, 124)
            .WithArguments("extra", "p");

        await AnalyzerTestHelper.RunAsync<PromptInputPlaceholderMismatchAnalyzer>(src, files, expected);
    }

    [Fact]
    public async Task Error_WhenInputsUnknownAtAnalysisTime()
    {
        string src = @"using Revi; using System.Collections.Generic; class C { void M(){ List<Input> list = Get(); _ = Infer.ToString(""p"", list); } List<Input> Get()=> new List<Input>(); }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/p.pmt", "[[information]]\nname = p\n\n[_instruction]\nHello ${user}!")
        ];

        DiagnosticResult expected = new DiagnosticResult(PromptInputPlaceholderMismatchAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithSpan("/0/Test1.cs", 1, 112, 1, 115)
            .WithArguments("p", "user");

        await AnalyzerTestHelper.RunAsync<PromptInputPlaceholderMismatchAnalyzer>(src, files, expected);
    }

    [Fact]
    public async Task NoDiagnostic_WhenPlaceholdersMatchInputs()
    {
        string src = "using Revi; using System.Collections.Generic; class C { void M(){ _ = Infer.ToString(\"p\", new List<Input>{ new Input(\"user\", \"A\"), new Input(\"city\", \"LA\") }); } }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/p.pmt", "[[information]]\nname = p\n\n[_instruction]\nHello ${user} in ${city}!")
        ];

        await AnalyzerTestHelper.RunAsync<PromptInputPlaceholderMismatchAnalyzer>(src, files);
    }
}
