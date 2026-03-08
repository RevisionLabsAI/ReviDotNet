// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>
/// Provides helper methods to run Roslyn analyzer tests with default Revi stubs and AdditionalFiles.
/// </summary>
internal static class AnalyzerTestHelper
{
    /// <summary>
    /// A minimal set of API stubs to let analyzers resolve symbols like <c>Revi.Infer</c> and <c>Revi.Input</c>.
    /// </summary>
    private const string DefaultReviStubs = @"namespace Revi {
public static class Infer {
    public static string ToString(string promptName) => string.Empty;
    public static string ToString(string promptName, System.Collections.Generic.List<Input>? inputs) => string.Empty;
    public static string ToStringList(string promptName) => string.Empty;
    public static string ToStringList(string promptName, System.Collections.Generic.List<Input>? inputs) => string.Empty;
    public static string ToStringListLimited(string promptName, int? maxLines = null, System.Func<string, bool>? evaluator = null) => string.Empty;
    public static T ToEnum<T>(string promptName) => default;
    public static string ToString(string promptName, double temperature = 0, double top_p = 1, double presence_penalty = 0, double frequency_penalty = 0, int inactivity_timeout_seconds = 0) => string.Empty;
    public static string ToString(string promptName, System.Threading.CancellationToken token = default) => string.Empty;
    public static string ToStringList(string promptName, System.Threading.CancellationToken token = default) => string.Empty;
    public static string ToStringListLimited(string promptName, int? maxLines, System.Func<string, bool>? evaluator, System.Threading.CancellationToken token = default) => string.Empty;
    public static bool ToBool(string promptName) => false;
    public static object ToJObject(string promptName) => new object();
    public static T? ToObject<T>(string promptName, System.Collections.Generic.List<Input>? inputs = null) => default;
    public static T? ToObject<T>(string promptName, System.Collections.Generic.List<Input>? inputs, System.Threading.CancellationToken token = default) => default;
    public static System.Collections.Generic.IAsyncEnumerable<string> Completion(string promptName)
    {
        return Impl();
        static async System.Collections.Generic.IAsyncEnumerable<string> Impl()
        {
            await System.Threading.Tasks.Task.CompletedTask;
            yield break;
        }
    }
    public static System.Collections.Generic.IAsyncEnumerable<string> Completion(string promptName, System.Threading.CancellationToken token = default) => Completion(promptName);
}
public class Input { public Input(string label, string text) { } }
}";

    /// <summary>
    /// Runs an analyzer test for a single C# source with optional AdditionalFiles and expected diagnostics.
    /// Adds Revi API stubs automatically.
    /// </summary>
    /// <typeparam name="TAnalyzer">The analyzer type.</typeparam>
    /// <param name="source">The C# source code under test.</param>
    /// <param name="additionalFiles">Additional files (path, content) to include in the compilation.</param>
    /// <param name="expected">Expected diagnostics.</param>
    public static async Task RunAsync<TAnalyzer>(
        string source,
        (string path, string content)[] additionalFiles,
        params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        CSharpAnalyzerTest<TAnalyzer, XUnitVerifier> test = new CSharpAnalyzerTest<TAnalyzer, XUnitVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net90,
        };

        // Add default Revi stubs and then the test source (existing tests reference Test1.cs)
        test.TestState.Sources.Add(DefaultReviStubs);
        test.TestState.Sources.Add(source);

        foreach ((string path, string content) in additionalFiles)
        {
            test.TestState.AdditionalFiles.Add((path, content));
        }

        foreach (DiagnosticResult d in expected)
        {
            test.ExpectedDiagnostics.Add(d);
        }

        await test.RunAsync();
    }
}
