using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using ReviDotNet.Tests.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers
{
    /// <summary>
    /// Tests for <see cref="ProviderProfileSchemaAnalyzer"/> (REVI041).
    /// </summary>
    public sealed class ProviderProfileSchemaAnalyzerTests
    {
        [Fact]
        public async Task ReportsError_OnInvalidProtocol()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Providers/bad.rcfg", @"[[general]]
name = p
protocol = Nope
api-url = https://example/
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerError(ProviderProfileSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Providers/bad.rcfg", 3, 1, 3, 1)
                .WithArguments("Nope", "general.protocol", " (allowed: OpenAI, vLLM, Gemini, LLamaAPI, Claude)");

            await AnalyzerTestHelper.RunAsync<ProviderProfileSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task ReportsWarning_OnNegativeTimeout()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Providers/neg.rcfg", @"[[general]]
name = p
protocol = OpenAI
api-url = https://api.openai.com/v1/

[[limiting]]
timeout-seconds = -10
")
            };

            DiagnosticResult expected = DiagnosticResult.CompilerWarning(ProviderProfileSchemaAnalyzer.DiagnosticId)
                .WithSpan("RConfigs/Providers/neg.rcfg", 7, 1, 7, 1)
                .WithArguments("-10", "limiting.timeout-seconds", " (>= 0)");

            await AnalyzerTestHelper.RunAsync<ProviderProfileSchemaAnalyzer>(code, files, expected);
        }

        [Fact]
        public async Task NoDiagnostic_OnValidProvider()
        {
            string code = "class C { void M() {} }";
            (string path, string content)[] files =
            {
                ("RConfigs/Providers/ok.rcfg", @"[[general]]
name = claude
enabled = true
protocol = Claude
api-url = https://api.anthropic.com/
api-key = environment
default-model = claude-3-5-sonnet-latest
supports-prompt-completion = true

[[guidance]]
supports-guidance = false
default-guidance-type = disabled

[[limiting]]
timeout-seconds = 300
delay-between-requests-ms = 20
retry-attempt-limit = 5
retry-initial-delay-seconds = 5
simultaneous-requests = 5
")
            };

            await AnalyzerTestHelper.RunAsync<ProviderProfileSchemaAnalyzer>(code, files);
        }
    }
}
