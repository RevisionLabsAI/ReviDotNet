// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using ReviDotNet.Analyzers;
using Xunit;

namespace ReviDotNet.Tests.Analyzers;

/// <summary>
/// Tests for REVI020: ToEnum<T> with non-enum T.
/// </summary>
public sealed class ToEnumGenericTypeAnalyzerTests
{
    [Fact]
    public async Task Reports_When_Generic_Is_Not_Enum()
    {
        string src = @"using Revi; class C { void M(){ var s = Infer.ToEnum<int>(""MyPrompt""); } }";
        DiagnosticResult expected = new DiagnosticResult(ToEnumGenericTypeAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan("/0/Test1.cs", 1, 47, 1, 58);
        await AnalyzerTestHelper.RunAsync<ToEnumGenericTypeAnalyzer>(src, [], expected);
    }

    [Fact]
    public async Task No_Report_When_Generic_Is_Enum()
    {
        string src = @"using Revi; enum E{A} class C { void M(){ var s = Infer.ToEnum<E>(""MyPrompt""); } }";
        await AnalyzerTestHelper.RunAsync<ToEnumGenericTypeAnalyzer>(src, []);
    }
}
