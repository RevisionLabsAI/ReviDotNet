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
/// Tests for REVI005: BrokenRConfigsLinkageAnalyzer.
/// </summary>
public sealed class BrokenRConfigsLinkageAnalyzerTests
{
    [Fact]
    public async Task Error_WhenModelProfileNotFound()
    {
        string src = "class C { }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/p.pmt", "[[information]]\nname = p\n\n[[settings]]\nmodel_profile = does-not-exist\n"),
            ("C:/proj/RConfigs/Models/Inference/existing.rcfg", "[[general]]\nname = existing-model\n")
        ];

        DiagnosticResult expected = new DiagnosticResult(BrokenRConfigsLinkageAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithSpan("C:/proj/RConfigs/Prompts/p.pmt", 5, 17, 5, 18)
            .WithArguments("does-not-exist");
        await AnalyzerTestHelper.RunAsync<BrokenRConfigsLinkageAnalyzer>(src, files, expected);
    }

    [Fact]
    public async Task NoDiagnostic_WhenReferencesExist()
    {
        string src = "class C { }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/p.pmt", "[[information]]\nname = p\n\n[[settings]]\nmodel_profile = existing-model\nprovider_profile = provider-a\n"),
            ("C:/proj/RConfigs/Models/Inference/existing.rcfg", "[[general]]\nname = existing-model\n"),
            ("C:/proj/RConfigs/Providers/a.rcfg", "[[general]]\nname = provider-a\n")
        ];

        await AnalyzerTestHelper.RunAsync<BrokenRConfigsLinkageAnalyzer>(src, files);
    }

    /// <summary>
    /// A declared name read from a CRLF file (or one with trailing spaces) must not keep the '\r' or blanks:
    /// the runtime registers the trimmed name, so the analyzer must not report a false REVI005.
    /// </summary>
    [Fact]
    public async Task NoDiagnostic_WhenConfigFilesUseCrlfOrTrailingSpaces()
    {
        string src = "class C { }";
        (string, string)[] files =
        [
            ("C:/proj/RConfigs/Prompts/p.pmt", "[[information]]\r\nname = p\r\n\r\n[[settings]]\r\nmodel_profile = existing-model\r\nprovider_profile = provider-a\r\n"),
            ("C:/proj/RConfigs/Models/Inference/existing.rcfg", "[[general]]\r\nname = existing-model\r\n"),
            ("C:/proj/RConfigs/Providers/a.rcfg", "general_name = provider-a   \n")
        ];

        await AnalyzerTestHelper.RunAsync<BrokenRConfigsLinkageAnalyzer>(src, files);
    }
}
