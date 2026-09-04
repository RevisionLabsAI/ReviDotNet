// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Pins the two halves of the output-shape guard: a model answer wrapped under a type-name root
/// is unwrapped when the inner object is what conforms, and a prompt whose examples teach that
/// shape is warned about rather than silently tolerated. Thirteen BetterNamer prompts carried
/// examples of the form <c>{"TypeName": {...}}</c> against a schema for the bare object; the
/// provider with a post-hoc validator (Groq) rejected every call, and a provider with no
/// enforcement would have returned the wrapper, deserializing to an object with every field null.
/// </summary>
public sealed class JsonOutputShapeGuardTests
{
    /// <summary>A stand-in for a prompt's output type.</summary>
    private sealed class Classification
    {
        /// <summary>The category.</summary>
        public string? Category { get; set; }

        /// <summary>The confidence.</summary>
        public string? Confidence { get; set; }
    }

    /// <summary>A type whose single property is itself an object — the shape a wrapper is easily mistaken for.</summary>
    private sealed class Envelope
    {
        /// <summary>The payload.</summary>
        public Classification? Classification { get; set; }
    }

    /// <summary>The wrapper is stripped when only the inner object conforms.</summary>
    [Fact]
    public void A_type_name_wrapper_is_unwrapped_when_the_inner_object_conforms()
    {
        const string wrapped = """{"Classification":{"category":"Allowed","confidence":"High"}}""";

        bool unwrapped = JsonOutputValidation.TryUnwrapSingleRoot(wrapped, typeof(Classification), "test-prompt", out string result);

        unwrapped.Should().BeTrue();
        result.Should().Be("""{"category":"Allowed","confidence":"High"}""");
    }

    /// <summary>An answer that already conforms is left alone, even when it looks like a wrapper.</summary>
    [Fact]
    public void A_conforming_single_property_object_is_not_mistaken_for_a_wrapper()
    {
        const string envelope = """{"classification":{"category":"Allowed","confidence":"High"}}""";

        bool unwrapped = JsonOutputValidation.TryUnwrapSingleRoot(envelope, typeof(Envelope), "test-prompt", out string result);

        unwrapped.Should().BeFalse("the outer object is the Envelope the caller asked for");
        result.Should().Be(envelope);
    }

    /// <summary>A bare conforming answer, a non-object, an empty string and unparseable text all pass through untouched.</summary>
    [Theory]
    [InlineData("""{"category":"Allowed","confidence":"High"}""")]
    [InlineData("""{"Wrapper":{"Nothing":"useful"}}""")]
    [InlineData("""[{"category":"Allowed"}]""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Anything_else_passes_through_unchanged(string json)
    {
        bool unwrapped = JsonOutputValidation.TryUnwrapSingleRoot(json, typeof(Classification), "test-prompt", out string result);

        unwrapped.Should().BeFalse();
        result.Should().Be(json);
    }

    /// <summary>The example check never throws and tolerates examples that are not JSON — it is a warning, not a gate.</summary>
    [Fact]
    public void The_example_check_is_advisory_and_never_throws()
    {
        Prompt prompt = new()
        {
            Name = "shape-guard-test-" + Guid.NewGuid().ToString("N"),
            RequestJson = true,
            Examples =
            [
                new Example([], """{"Classification":{"category":"Allowed","confidence":"High"}}"""),
                new Example([], """{"category":"Allowed","confidence":"High"}"""),
                new Example([], "prose, not json"),
                new Example([], ""),
            ],
        };

        Action check = () =>
        {
            JsonOutputValidation.WarnIfExamplesDoNotConform(prompt, typeof(Classification));
            JsonOutputValidation.WarnIfExamplesDoNotConform(prompt, typeof(Classification)); // memoized second pass
            JsonOutputValidation.WarnIfExamplesDoNotConform(prompt, null);
            JsonOutputValidation.WarnIfExamplesDoNotConform(new Prompt { Name = "no-examples" }, typeof(Classification));
        };

        check.Should().NotThrow();
    }
}
