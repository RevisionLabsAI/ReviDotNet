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
    /// Tests for <see cref="AgentGraphAnalyzer"/> (REVI011): build-time validation of .agent state graphs
    /// (T16/T17/T18b).
    /// </summary>
    public sealed class AgentGraphAnalyzerTests
    {
        private const string Code = "class C { void M() {} }";
        private const string Path = "RConfigs/Agents/Sample.agent";

        [Fact]
        public async Task Warns_OnUndefinedTransitionTarget()
        {
            (string path, string content)[] files =
            {
                (Path, @"[[information]]
name = ok
[[loop]]
entry = a
[[state.a]]
description = A
[[_loop]]
a
  -> nowhere [when: GO]
  -> [end]
")
            };

            DiagnosticResult expected = new DiagnosticResult(AgentGraphAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(Path, 9, 1, 9, 1)
                .WithArguments("nowhere");

            await AnalyzerTestHelper.RunAsync<AgentGraphAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task Warns_OnDeadEdgeAfterUnconditionalFallback()
        {
            (string path, string content)[] files =
            {
                (Path, @"[[information]]
name = ok
[[loop]]
entry = a
[[state.a]]
description = A
[[_loop]]
a
  -> [end]
  -> a [when: GO]
")
            };

            DiagnosticResult expected = new DiagnosticResult(AgentGraphAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(Path, 10, 1, 10, 1)
                .WithArguments("a");

            await AnalyzerTestHelper.RunAsync<AgentGraphAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task Warns_OnDuplicateSignal()
        {
            (string path, string content)[] files =
            {
                (Path, @"[[information]]
name = ok
[[loop]]
entry = a
[[state.a]]
description = A
[[_loop]]
a
  -> [end] [when: GO]
  -> a [when: GO]
")
            };

            DiagnosticResult expected = new DiagnosticResult(AgentGraphAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(Path, 10, 1, 10, 1)
                .WithArguments("GO");

            await AnalyzerTestHelper.RunAsync<AgentGraphAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task Warns_OnUnderscoreStateName()
        {
            (string path, string content)[] files =
            {
                (Path, @"[[information]]
name = ok
[[loop]]
entry = good_state
[[state.good_state]]
description = X
[[_loop]]
good_state
  -> [end]
")
            };

            DiagnosticResult expected = new DiagnosticResult(AgentGraphAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(Path, 5, 1, 5, 1)
                .WithArguments("good_state");

            await AnalyzerTestHelper.RunAsync<AgentGraphAnalyzer>(Code, files, expected);
        }

        [Fact]
        public async Task NoWarning_OnCleanGraph()
        {
            (string path, string content)[] files =
            {
                (Path, @"[[information]]
name = ok
[[loop]]
entry = a
[[state.a]]
description = A
[[state.b]]
description = B
[[_loop]]
a
  -> b [when: GO]
  -> [end]
b
  -> [end]
")
            };

            await AnalyzerTestHelper.RunAsync<AgentGraphAnalyzer>(Code, files);
        }
    }
}
