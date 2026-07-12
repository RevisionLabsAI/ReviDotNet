// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using ReviDotNet.Tests.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers
{
    /// <summary>
    /// Tests for <see cref="ModelProfileSchemaAnalyzer"/> (REVI040).
    /// </summary>
    public sealed class ModelProfileSchemaAnalyzerTests
    {
        [Fact]
        public async Task ReportsError_OnMissingGeneralName()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Models/Inference/sample.rcfg", @"[[general]]
model-string = x
provider-name = y
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerError(ModelProfileSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Models/Inference/sample.rcfg", 1, 1, 1, 1)
                .WithArguments("<missing>", "general.name", " (required)");

            await AnalyzerTestHelper.RunAsync<ModelProfileSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task ReportsWarning_OnNegativeTokenLimit()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Models/Inference/neg.rcfg", @"[[general]]
name = modelA
model-string = id
provider-name = prov

[[settings]]
context-window = -1
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerWarning(ModelProfileSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Models/Inference/neg.rcfg", 7, 1, 7, 1)
                .WithArguments("-1", "settings.context-window", " (>= 0)");

            await AnalyzerTestHelper.RunAsync<ModelProfileSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task NoDiagnostic_OnValidModel()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Models/Inference/ok.rcfg", @"[[general]]
name = anth_sonnet_35
enabled = true
model-string = claude-3-5-sonnet-latest
provider-name = claude

[[settings]]
tier = A
context-window = 100000
")
            };

            await AnalyzerTestHelper.RunAsync<ModelProfileSchemaAnalyzer>(code, files);
        }

        // D37: a 'listed'/'both' input type with no single-item/multi-item template warns at build time.

        [Fact]
        public async Task ReportsWarning_OnListedInputTypeWithoutTemplates()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Models/Inference/notmpl.rcfg", @"[[general]]
name = m
model-string = id
provider-name = prov

[[input]]
default-instruction-input-type = listed
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerWarning(ModelProfileSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Models/Inference/notmpl.rcfg", 7, 1, 7, 1)
                .WithArguments("single-item and multi-item");

            await AnalyzerTestHelper.RunAsync<ModelProfileSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task NoWarning_WhenInputTemplatesPresent()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Models/Inference/withtmpl.rcfg", @"[[general]]
name = m
model-string = id
provider-name = prov

[[input]]
default-instruction-input-type = listed
single-item = {label}: {text}
multi-item = Input #{iterator}: {label}: {text}
")
            };

            await AnalyzerTestHelper.RunAsync<ModelProfileSchemaAnalyzer>(code, files);
        }
    }
}
