// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using ReviDotNet.Tests.Agents;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Regression tests for the Batch 2 design-improvement fixes:
/// T12/T14 (guidance capability matrix), T16/T17/T18b (agent-graph validation).
/// (T11/T13/REVI010/REVI011 analyzer behavior is covered in the Analyzers tests;
/// T20/T18a/T19 in ToolRenderingTests.)
/// </summary>
public class TBatch2FixesTests
{
    // ── T12 + T14: provider×decode-mode capability matrix ──

    [Theory]
    [InlineData(Protocol.OpenAI, GuidanceType.Json, true)]
    [InlineData(Protocol.OpenAI, GuidanceType.Regex, false)]
    [InlineData(Protocol.Perplexity, GuidanceType.Json, true)]
    [InlineData(Protocol.Gemini, GuidanceType.Json, true)]
    [InlineData(Protocol.Gemini, GuidanceType.Regex, false)]
    [InlineData(Protocol.vLLM, GuidanceType.Json, true)]
    [InlineData(Protocol.vLLM, GuidanceType.Regex, true)]
    [InlineData(Protocol.vLLM, GuidanceType.Grammar, false)]
    [InlineData(Protocol.LLamaAPI, GuidanceType.Json, true)]
    [InlineData(Protocol.LLamaAPI, GuidanceType.Grammar, true)]
    [InlineData(Protocol.LLamaAPI, GuidanceType.Regex, false)]
    [InlineData(Protocol.Claude, GuidanceType.Json, true)]
    [InlineData(Protocol.Claude, GuidanceType.Regex, false)]
    public void GuidanceCapability_Supports_MatchesPayloadTransformer(Protocol protocol, GuidanceType type, bool expected)
    {
        GuidanceCapability.Supports(protocol, type).Should().Be(expected);
    }

    [Fact]
    public void GuidanceCapability_NullOrDisabled_IsAlwaysSupported()
    {
        GuidanceCapability.Supports(Protocol.Claude, null).Should().BeTrue();
        GuidanceCapability.Supports(Protocol.OpenAI, GuidanceType.Disabled).Should().BeTrue();
    }

    // ── T16/T17/T18b: AgentProfile.ValidateGraph surfaces graph mistakes ──

    [Fact]
    public void ValidateGraph_FlagsUndefinedTransitionTarget()
    {
        AgentProfile profile = AgentBuilder.FromText(@"
[[information]]
name = t
[[loop]]
entry = a
[[state.a]]
description = A
[[_loop]]
a
  -> nowhere [when: GO]
  -> [end]
")!;

        profile.ValidateGraph().Should().ContainSingle(w => w.Contains("'nowhere'") && w.Contains("not a defined state"));
    }

    [Fact]
    public void ValidateGraph_FlagsDeadEdgeAfterUnconditionalFallback()
    {
        AgentProfile profile = AgentBuilder.FromText(@"
[[information]]
name = t
[[loop]]
entry = a
[[state.a]]
description = A
[[_loop]]
a
  -> [end]
  -> a [when: GO]
")!;

        profile.ValidateGraph().Should().Contain(w => w.Contains("unreachable") && w.Contains("unconditional"));
    }

    [Fact]
    public void ValidateGraph_FlagsDuplicateSignal()
    {
        AgentProfile profile = AgentBuilder.FromText(@"
[[information]]
name = t
[[loop]]
entry = a
[[state.a]]
description = A
[[_loop]]
a
  -> [end] [when: GO]
  -> a [when: GO]
")!;

        profile.ValidateGraph().Should().Contain(w => w.Contains("GO") && w.Contains("more than once"));
    }

    [Fact]
    public void ValidateGraph_FlagsUnderscoreStateName()
    {
        AgentProfile profile = AgentBuilder.FromText(@"
[[information]]
name = t
[[loop]]
entry = a
[[state.a]]
description = A
[[_loop]]
a
  -> [end]
bad_node
  -> [end]
")!;

        profile.ValidateGraph().Should().Contain(w => w.Contains("bad_node") && w.Contains("underscores"));
    }

    [Fact]
    public void ValidateGraph_CleanGraph_HasNoWarnings()
    {
        AgentProfile profile = AgentBuilder.FromText(@"
[[information]]
name = t
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
")!;

        profile.ValidateGraph().Should().BeEmpty();
    }

    // ── T16/T17: CollectDiscoveryWarnings catches under-specified / underscore'd states ──

    [Fact]
    public void CollectDiscoveryWarnings_FlagsUndiscoveredStateSection()
    {
        // A state with only an instruction section and an underscore name is never discovered.
        var data = new Dictionary<string, string>
        {
            ["_state.resolve_conflict.instruction"] = "do the thing",
            ["state.search_description"] = "search",
        };
        var discovered = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "search" };

        List<string> warnings = AgentProfile.CollectDiscoveryWarnings(data, discovered, "t");

        warnings.Should().ContainSingle(w => w.Contains("resolve_conflict") && w.Contains("ignored"));
    }

    [Fact]
    public void CollectDiscoveryWarnings_AllDiscovered_NoWarnings()
    {
        var data = new Dictionary<string, string>
        {
            ["state.search_description"] = "search",
            ["_state.search.instruction"] = "go",
        };
        var discovered = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "search" };

        AgentProfile.CollectDiscoveryWarnings(data, discovered, "t").Should().BeEmpty();
    }
}
