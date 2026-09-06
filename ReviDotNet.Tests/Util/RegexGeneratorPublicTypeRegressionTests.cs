// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Util;

/// <summary>
/// Pins public and framework contract shapes that differed during the Newtonsoft.Json.Schema
/// migration. Every expected value was captured from version 4.0.1 at commit 819d655.
/// </summary>
public sealed class RegexGeneratorPublicTypeRegressionTests
{
    private sealed class ProbeException : Exception
    {
        public string Code { get; set; } = "";
    }

    private sealed class BaseExceptionHolder
    {
        public Exception Error { get; set; } = new();
    }

    private sealed class DerivedExceptionHolder
    {
        public ProbeException Error { get; set; } = new();
    }

    [Theory]
    [InlineData(
        typeof(BrowserHeaderProfile),
        168,
        "9B71261C86C3D6018E3E7ACB707CF834ED0867E29D31B136E7ABBB6B6E3AC175")]
    [InlineData(
        typeof(InferenceProviderException),
        595,
        "D2CE97B552ACBA7E47D0FAA4C190D1667F6FCF95086C9CB679E0A10A89764A89")]
    [InlineData(
        typeof(StreamingMetadata),
        277,
        "CD268889CE565F349681325F724D1E3958BF59C0A50F2F4EFD3EA12E592EE1B0")]
    [InlineData(
        typeof(StreamingMetadataTracker),
        781,
        "58DB8CED286F142112B04B99F41F50A25A69857FC11DE32FBE7F134D93D2AEA3")]
    public void FromObject_matches_legacy_public_type_contract(
        Type type,
        int expectedLength,
        string expectedHash)
    {
        AssertLegacyHash(type, expectedLength, expectedHash);
    }

    [Fact]
    public void FromObject_preserves_root_and_nested_exception_contract_kinds()
    {
        const string rootDerived =
            "(?:\\{\\s*\"Code\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Message\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Data\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
            "\"InnerException\"\\s*:\\s*(?:\\{\\s*\\s*\\}),\\s*" +
            "\"HelpLink\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"Source\"\\s*:\\s*(?:\"[^\"]*\"),\\s*" +
            "\"HResult\"\\s*:\\s*(?:\\d+),\\s*" +
            "\"StackTrace\"\\s*:\\s*(?:\"[^\"]*\")\\s*\\})";
        const string nestedBase =
            "(?:\\{\\s*\"Error\"\\s*:\\s*(?:\\{\\s*\\s*\\})\\s*\\})";
        const string nestedDerived =
            "(?:\\{\\s*\"Error\"\\s*:\\s*" + rootDerived + "\\s*\\})";

        AssertExactLegacyContract(
            typeof(ProbeException),
            rootDerived,
            "5AA5FEA237EF849CEFA0CF2B7DF5E3CE4A98228B25B8F5F6F8A87F90C0EBAD88");
        AssertExactLegacyContract(
            typeof(BaseExceptionHolder),
            nestedBase,
            "19214AADF457C21CC14222204832598C5FB734D8AD8E2B19334892CC8D15F9EB");
        AssertExactLegacyContract(
            typeof(DerivedExceptionHolder),
            nestedDerived,
            "0341240A0EA72ECE0DA1C38C3A63B1B4CF45793F2FEE16F2EE43B959B7466B14");
    }

    [Fact]
    public void FromObject_preserves_legacy_empty_KeyValuePair_schema_and_array_items()
    {
        const string root = "(?:)";
        const string list = "(?:\\[\\s*(?:)(?:\\s*,\\s*(?:))*?\\s*\\])";

        AssertExactLegacyContract(
            typeof(KeyValuePair<string, string>),
            root,
            "EA895804EC95C8A074AE5C20D03E18DF300996DA22333F4E0CED52E42A9A255B");
        AssertExactLegacyContract(
            typeof(List<KeyValuePair<string, string>>),
            list,
            "BB6C4DFD472DF61084B4D9607B194A37B168BAF0471D24DC5EB01C4D8E9EB8EB");
    }

    [Fact]
    public void FromObject_preserves_legacy_empty_base_exception_schema_and_array_items()
    {
        const string root = "(?:\\{\\s*\\s*\\})";
        const string list =
            "(?:\\[\\s*(?:\\{\\s*\\s*\\})(?:\\s*,\\s*(?:\\{\\s*\\s*\\}))*?\\s*\\])";

        AssertExactLegacyContract(
            typeof(Exception),
            root,
            "B74D02BAE6788711365C01A3862ED95F6FC9A005780A71A83BB5B705C392F4E4");
        AssertExactLegacyContract(
            typeof(List<Exception>),
            list,
            "80EE2784D872D9A3886E1BD3DC93BDD0099DAE8D5ED7DEC3E9D1082D827BE78D");
    }

    private static void AssertExactLegacyContract(
        Type type,
        string expectedRegex,
        string expectedHash)
    {
        string regex = RegexGenerator.FromObject(type, chainOfThought: false);

        regex.Should().Be(expectedRegex);
        Hash(regex).Should().Be(expectedHash);
    }

    private static void AssertLegacyHash(Type type, int expectedLength, string expectedHash)
    {
        string regex = RegexGenerator.FromObject(type, chainOfThought: false);

        regex.Should().HaveLength(expectedLength);
        Hash(regex).Should().Be(expectedHash);
    }

    private static string Hash(string regex)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(regex)));
    }
}
