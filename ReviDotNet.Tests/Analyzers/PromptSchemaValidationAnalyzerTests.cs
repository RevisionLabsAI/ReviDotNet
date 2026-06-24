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
    /// Tests for <see cref="PromptSchemaValidationAnalyzer"/> (REVI010): cross-validates [[_schema]] vs the
    /// guidance-schema-type strategy (T13).
    /// </summary>
    public sealed class PromptSchemaValidationAnalyzerTests
    {
        private const string Code = "class C { void M() {} }";

        [Fact]
        public async Task Warns_OnManualStrategyWithoutSchema()
        {
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[settings]]
guidance-schema-type = json-manual
")
            };

            DiagnosticResult expected = new DiagnosticResult(PromptSchemaValidationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan("RConfigs/Prompts/Sample.pmt", 2, 1, 2, 1)
                .WithArguments("json-manual");

            await AnalyzerTestHelper.RunAsync<PromptSchemaValidationAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task Warns_OnOrphanedSchemaWithAutoStrategy()
        {
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[settings]]
guidance-schema-type = json-auto
[[_schema]]
{ ""type"": ""object"" }
")
            };

            DiagnosticResult expected = new DiagnosticResult(PromptSchemaValidationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan("RConfigs/Prompts/Sample.pmt", 3, 1, 3, 1)
                .WithArguments("= 'json-auto' (not a manual strategy)");

            await AnalyzerTestHelper.RunAsync<PromptSchemaValidationAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task Warns_OnInvalidJsonManualSchema()
        {
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[settings]]
guidance-schema-type = json-manual
[[_schema]]
{ ""type"": ""object""
")
            };

            DiagnosticResult expected = new DiagnosticResult(PromptSchemaValidationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan("RConfigs/Prompts/Sample.pmt", 3, 1, 3, 1);

            await AnalyzerTestHelper.RunAsync<PromptSchemaValidationAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task NoWarning_OnValidJsonManualSchema()
        {
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[settings]]
guidance-schema-type = json-manual
[[_schema]]
{ ""type"": ""object"" }
")
            };

            await AnalyzerTestHelper.RunAsync<PromptSchemaValidationAnalyzer>(Code, files);
        }
    }
}
