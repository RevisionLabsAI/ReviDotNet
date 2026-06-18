// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers
{
    /// <summary>
    /// Tests for <see cref="PromptExamplePairingAnalyzer"/> (REVI009): flags few-shot example halves
    /// that are missing their counterpart (T10).
    /// </summary>
    public sealed class PromptExamplePairingAnalyzerTests
    {
        private const string Code = "class C { void M() {} }";

        [Fact]
        public async Task ReportsWarning_OnExampleMissingOutput()
        {
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[information]]
name = ok

[[_exin_1]]
a
[[_exout_1]]
b
[[_exin_2]]
c
")
            };

            // _exin_2 is on line 8 (1-based) and has no matching _exout_2.
            DiagnosticResult expected = new DiagnosticResult(PromptExamplePairingAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan("RConfigs/Prompts/Sample.pmt", 8, 1, 8, 1)
                .WithArguments(2, "output", "_exout_2");

            await AnalyzerTestHelper.RunAsync<PromptExamplePairingAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task ReportsWarning_OnExampleMissingInput()
        {
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[information]]
name = ok

[[_exin_1]]
a
[[_exout_1]]
b
[[_exout_3]]
d
")
            };

            // _exout_3 is on line 8 (1-based) and has no matching _exin_3.
            DiagnosticResult expected = new DiagnosticResult(PromptExamplePairingAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan("RConfigs/Prompts/Sample.pmt", 8, 1, 8, 1)
                .WithArguments(3, "input", "_exin_3");

            await AnalyzerTestHelper.RunAsync<PromptExamplePairingAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task NoWarning_WhenAllExamplesPaired()
        {
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[information]]
name = ok

[[_exin_1]]
a
[[_exout_1]]
b
[[_exin_2]]
c
[[_exout_2]]
d
")
            };

            await AnalyzerTestHelper.RunAsync<PromptExamplePairingAnalyzer>(Code, files);
        }
    }
}
