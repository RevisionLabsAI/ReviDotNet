// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// Regression tests for model, provider and embedding naming, the sibling of
/// <see cref="PromptRegistryNameTests"/>.
/// <para>
/// A config must register under its declared <c>name</c> verbatim. The loaders used to prefix the
/// name with a lowercased folder path derived by splitting the resource name on '.', which is
/// unsound for two separate reasons: a subfoldered config became unreachable under its declared
/// name, and — because .NET mangles folder separators and dots into the same character when it
/// embeds a resource — a file whose own name contains a dot had part of that name read as a
/// folder. <c>Models/Inference/gpt-5.6-sol.rcfg</c> registered as <c>gpt-5/gpt-5.6-sol</c>, so a
/// deployment that configured the model as <c>gpt-5.6-sol</c> was told no such model existed.
/// </para>
/// </summary>
public sealed class ConfigRegistryNameTests
{
    private static void InTempRoot(Action<string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "revi-config-name-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            body(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ModelWhoseFileNameContainsDots_registers_under_its_declared_name()
    {
        InTempRoot(root =>
        {
            string dir = Path.Combine(root, "Models", "Inference");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "gpt-5.6-sol.rcfg"),
                "[[general]]\nname = gpt-5.6-sol\nenabled = true\nmodel-string = gpt-5.6-sol\n" +
                "provider-name = openai\n");

            ModelManagerService models = new(
                new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>()),
                new RecordingReviLogger<ModelManagerService>());
            models.LoadDirectory(root);

            models.GetAll().Should().ContainSingle()
                .Which.Name.Should().Be("gpt-5.6-sol",
                    "a dot inside a file name is part of the name, not a folder separator");
            models.Get("gpt-5.6-sol").Should().NotBeNull(
                "a deployment configures this model by the name its file declares");
        });
    }

    [Fact]
    public void SubfolderedModel_registers_under_its_declared_name()
    {
        InTempRoot(root =>
        {
            string dir = Path.Combine(root, "Models", "Inference", "openai");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mini.rcfg"),
                "[[general]]\nname = gpt-4o-mini\nenabled = true\nmodel-string = gpt-4o-mini\n" +
                "provider-name = openai\n");

            ModelManagerService models = new(
                new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>()),
                new RecordingReviLogger<ModelManagerService>());
            models.LoadDirectory(root);

            models.GetAll().Should().ContainSingle()
                .Which.Name.Should().Be("gpt-4o-mini",
                    "subfolders are organizational only (no 'openai/' prefix)");
        });
    }

    [Fact]
    public void SubfolderedProvider_registers_under_its_declared_name()
    {
        InTempRoot(root =>
        {
            string dir = Path.Combine(root, "Providers", "hosted");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "openai.rcfg"),
                "[[general]]\nname = openai\nenabled = true\nprotocol = OpenAI\n" +
                "api-url = https://api.openai.com\ndefault-model = gpt-4o-mini\n");

            ProviderManagerService providers = new(new RecordingReviLogger<ProviderManagerService>());
            providers.LoadDirectory(root);

            providers.GetAll().Select(p => p.Name).Should().Equal("openai");
            providers.Get("openai").Should().NotBeNull(
                "a model's provider-name is matched against the declared provider name");
        });
    }

    [Fact]
    public void SubfolderedModel_still_resolves_its_provider()
    {
        InTempRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "Providers"));
            File.WriteAllText(Path.Combine(root, "Providers", "openai.rcfg"),
                "[[general]]\nname = openai\nenabled = true\nprotocol = OpenAI\n" +
                "api-url = https://api.openai.com\ndefault-model = gpt-4o-mini\n");

            string modelDir = Path.Combine(root, "Models", "Inference", "frontier");
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Combine(modelDir, "gpt-5.6-sol.rcfg"),
                "[[general]]\nname = gpt-5.6-sol\nenabled = true\nmodel-string = gpt-5.6-sol\n" +
                "provider-name = openai\n");

            ProviderManagerService providers = new(new RecordingReviLogger<ProviderManagerService>());
            providers.LoadDirectory(root);
            ModelManagerService models = new(providers, new RecordingReviLogger<ModelManagerService>());
            models.LoadDirectory(root);

            ModelProfile model = models.Get("gpt-5.6-sol")!;
            model.Should().NotBeNull();
            model.Enabled.Should().BeTrue("its provider resolved, so the model stays enabled");
            model.Provider.Name.Should().Be("openai");
        });
    }
}
