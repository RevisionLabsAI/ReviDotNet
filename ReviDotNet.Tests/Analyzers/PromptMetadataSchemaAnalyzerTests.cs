using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using ReviDotNet.Tests.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers
{
    /// <summary>
    /// Tests for <see cref="PromptMetadataSchemaAnalyzer"/> (REVI006).
    /// </summary>
    public sealed class PromptMetadataSchemaAnalyzerTests
    {
        [Fact]
        public async Task ReportsError_OnEmptyInformationName()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample.pmt", @"[[information]]
name = 
version = 1

[[_system]]
You are a test
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerError(PromptMetadataSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Prompts/Sample.pmt", 2, 1, 2, 1)
                .WithArguments("key", "information.name");

            await AnalyzerTestHelper.RunAsync<PromptMetadataSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task ReportsError_OnInvalidGuidanceSchemaType()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample2.pmt", @"[[information]]
name = ok

[[settings]]
guidance-schema-type = nope

[[_system]]
sys
[[_instruction]]
instr
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerError(PromptMetadataSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Prompts/Sample2.pmt", 5, 1, 5, 1)
                .WithArguments("nope", "settings.guidance-schema-type", "disabled, default, regex-manual, regex-auto, json-manual, json-auto, gnbf-manual, gnbf-auto");

            await AnalyzerTestHelper.RunAsync<PromptMetadataSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task ReportsWarning_OnOutOfRangeTemperature()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample3.pmt", @"[[information]]
name = ok

[[tuning]]
temperature = 3.5

[[_system]]
sys
[[_instruction]]
instr
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerWarning(PromptMetadataSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Prompts/Sample3.pmt", 5, 1, 5, 1)
                .WithArguments("3.5", "tuning.temperature", "[0, 2]");

            await AnalyzerTestHelper.RunAsync<PromptMetadataSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task ReportsWarning_OnNegativeTimeout()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample4.pmt", @"[[information]]
name = ok

[[settings]]
timeout = -5

[[_system]]
sys
[[_instruction]]
instr
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerWarning(PromptMetadataSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Prompts/Sample4.pmt", 5, 1, 5, 1)
                .WithArguments("-5", "settings.timeout", ">= 0");

            await AnalyzerTestHelper.RunAsync<PromptMetadataSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task ReportsWarning_OnUnknownKey()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Sample5.pmt", @"[[information]]
name = ok

[[settings]]
unknown-key = value

[[_system]]
sys
[[_instruction]]
instr
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerWarning(PromptMetadataSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Prompts/Sample5.pmt", 5, 1, 5, 1)
                .WithArguments("unknown-key", "settings");

            await AnalyzerTestHelper.RunAsync<PromptMetadataSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task NoDiagnostic_OnValidMetadata()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Prompts/Ok.pmt", @"[[information]]
name = ok
version = 1

[[settings]]
guidance-schema-type = default

[[tuning]]
temperature = 1.0

[[_system]]
You are fine.
[[_instruction]]
Proceed normally.
")
            };

            await AnalyzerTestHelper.RunAsync<PromptMetadataSchemaAnalyzer>(code, files);
        }
    }
}
