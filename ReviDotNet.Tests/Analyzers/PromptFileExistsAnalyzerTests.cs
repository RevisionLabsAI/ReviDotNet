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
/// Tests for REVI001: PromptFileExistsAnalyzer.
/// </summary>
public sealed class PromptFileExistsAnalyzerTests
{
    [Fact]
    public async Task NoDiagnostic_WhenPromptExists()
    {
        string src = "using Revi; class C { void M(){ _ = Infer.ToString(\"folder/my-prompt\"); } }";

        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/folder/anyname.pmt",
             "[[information]]\nname = my-prompt\n")
        ];

        await AnalyzerTestHelper.RunAsync<PromptFileExistsAnalyzer>(src, files);
    }

    [Fact]
    public async Task Error_WhenPromptMissing()
    {
        string src = "using Revi; class C { void M(){ _ = Infer.ToString(/*0*/\"folder/missing\"/*0*/); } }";

        DiagnosticResult expected = new DiagnosticResult(PromptFileExistsAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithSpan("/0/Test1.cs", 1, 57, 1, 73)
            .WithArguments("folder/missing");

        await AnalyzerTestHelper.RunAsync<PromptFileExistsAnalyzer>(src, [], expected);
    }

    // D15: the analyzers must also fire on the injected IInferService surface (not just the static Infer class).

    [Fact]
    public async Task Error_WhenPromptMissing_ViaInjectedService()
    {
        string src = "using Revi; class C { void M(IInferService infer){ _ = infer.ToString({|REVI001:\"folder/missing\"|}); } }";

        await AnalyzerTestHelper.RunAsync<PromptFileExistsAnalyzer>(src, []);
    }

    [Fact]
    public async Task NoDiagnostic_WhenPromptExists_ViaInjectedService()
    {
        string src = "using Revi; class C { void M(IInferService infer){ _ = infer.ToString(\"folder/my-prompt\"); } }";

        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/folder/anyname.pmt",
             "[[information]]\nname = my-prompt\n")
        ];

        await AnalyzerTestHelper.RunAsync<PromptFileExistsAnalyzer>(src, files);
    }

    // D68: the analyzer now mirrors the runtime — `:` separators and quoted names don't resolve,
    // so a prompt declared that way is (correctly) reported as not found.

    [Fact]
    public async Task Error_WhenNameUsesColonSeparator()
    {
        // Runtime RConfigParser splits only on '=', so `name : p` never sets the name → prompt doesn't exist.
        string src = "using Revi; class C { void M(){ _ = Infer.ToString({|REVI001:\"folder/p\"|}); } }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/folder/x.pmt", "[[information]]\nname : p\n")
        ];

        await AnalyzerTestHelper.RunAsync<PromptFileExistsAnalyzer>(src, files);
    }

    [Fact]
    public async Task Error_WhenNameQuoted()
    {
        // Runtime does NOT strip quotes, so the effective name is folder/"p" — referencing folder/p doesn't match.
        string src = "using Revi; class C { void M(){ _ = Infer.ToString({|REVI001:\"folder/p\"|}); } }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/folder/x.pmt", "[[information]]\nname = \"p\"\n")
        ];

        await AnalyzerTestHelper.RunAsync<PromptFileExistsAnalyzer>(src, files);
    }
}
