// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// Verifies that the config loaders are resilient: a single malformed file is logged and skipped
/// while the remaining valid files still load. Regression guard for the embedded-provider failure
/// where one bad file aborted the entire batch.
/// <para>
/// The loaders read from a path hardcoded in <c>LoadAsync</c> (BaseDirectory/RConfigs/...), so these
/// tests drive the private <c>LoadFromFileSystem(path)</c> directly against a controlled temp directory.
/// The embedded-resource loader uses the identical per-item try/catch pattern.
/// </para>
/// </summary>
public class LoaderResilienceTests
{
    [Fact]
    public void ProviderLoader_SkipsMalformedFile_AndLoadsValidOnes()
    {
        var logger = new RecordingReviLogger<ProviderManagerService>();
        var service = new ProviderManagerService(logger);

        RunInTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "good.rcfg"),
                "[[general]]\nname = good-provider\nenabled = true\nprotocol = OpenAI\n" +
                "api-url = https://example/v1/\ndefault-model = m\n");
            // protocol is an enum; an unknown value throws during deserialization.
            File.WriteAllText(Path.Combine(dir, "bad.rcfg"),
                "[[general]]\nname = bad-provider\nprotocol = NotARealProtocol\napi-url = https://example/\n");

            Action act = () => InvokeLoadFromFileSystem(service, dir);
            act.Should().NotThrow("one malformed file must not abort loading the rest");

            List<string?> names = service.GetAll().Select(p => p.Name).ToList();
            names.Should().Contain("good-provider");
            names.Should().NotContain("bad-provider");
            logger.Entries.Should().Contain(
                e => e.Level == LogLevel.Error && e.Message.Contains("bad.rcfg"),
                "the skipped file should be reported");
        });
    }

    [Fact]
    public void ModelLoader_SkipsMalformedFile_AndLoadsValidOnes()
    {
        // An empty provider registry is fine — models are added regardless of provider resolution.
        var providers = new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>());
        var logger = new RecordingReviLogger<ModelManagerService>();
        var service = new ModelManagerService(providers, logger);

        RunInTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "good.rcfg"),
                "[[general]]\nname = good-model\nenabled = true\nmodel-string = m\nprovider-name = p\n\n" +
                "[[settings]]\ntier = A\n");
            // tier is an enum; an unknown value throws during deserialization.
            File.WriteAllText(Path.Combine(dir, "bad.rcfg"),
                "[[general]]\nname = bad-model\nenabled = true\nmodel-string = m\nprovider-name = p\n\n" +
                "[[settings]]\ntier = NotATier\n");

            Action act = () => InvokeLoadFromFileSystem(service, dir);
            act.Should().NotThrow("one malformed file must not abort loading the rest");

            var names = service.GetAll().Select(m => m.Name).ToList();
            names.Should().Contain("good-model");
            names.Should().NotContain("bad-model");
            logger.Entries.Should().Contain(
                e => e.Level == LogLevel.Error && e.Message.Contains("bad.rcfg"),
                "the skipped file should be reported");
        });
    }

    [Fact]
    public void LoadDirectory_LoadsAgentFromExternalRConfigsRoot()
    {
        var service = new AgentManagerService(new RecordingReviLogger<AgentManagerService>());

        RunInTempDir(root =>
        {
            // Mirror the RConfigs/ layout: agents live under <root>/Agents/.
            Directory.CreateDirectory(Path.Combine(root, "Agents"));
            File.WriteAllText(Path.Combine(root, "Agents", "ext.agent"),
                "[[information]]\nname = ext-agent\nversion = 1\ndescription = external test agent\n\n" +
                "[[loop]]\nentry = start\n\n" +
                "[[state.start]]\ndescription = test state\n\n" +
                "[[_state.start.instruction]]\nReply and finish.\n\n" +
                "[[state.start.guardrails]]\nmax-steps = 2\ntimeout = 30\n\n" +
                "[[_loop]]\nstart\n  -> [end] [when: DONE]\n");

            service.LoadDirectory(root);

            var loaded = service.GetAll().SingleOrDefault(a => a.Name == "ext-agent");
            loaded.Should().NotBeNull("an agent in an additional RConfigs root should load");
            // The originating file is stamped so its source can be read back / edited even though it lives
            // outside the app's own RConfigs (the "couldn't load source" fix for external agents).
            loaded!.SourcePath.Should().Be(Path.GetFullPath(Path.Combine(root, "Agents", "ext.agent")));
            File.Exists(loaded.SourcePath!).Should().BeTrue();
        });
    }

    [Fact]
    public void LoadDirectory_IgnoresMissingFolders_AndDeduplicatesOnRepeatLoad()
    {
        var service = new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>());

        RunInTempDir(root =>
        {
            // No standard subfolders yet → LoadDirectory must be a quiet no-op, not throw.
            Action noop = () => service.LoadDirectory(root);
            noop.Should().NotThrow("a missing Providers/ subfolder is skipped");
            service.GetAll().Should().BeEmpty();

            Directory.CreateDirectory(Path.Combine(root, "Providers"));
            File.WriteAllText(Path.Combine(root, "Providers", "p.rcfg"),
                "[[general]]\nname = ext-provider\nenabled = true\nprotocol = OpenAI\n" +
                "api-url = https://example/v1/\ndefault-model = m\n");

            service.LoadDirectory(root);
            service.LoadDirectory(root); // repeat → CheckAdd dedups by name

            service.GetAll().Count(p => p.Name == "ext-provider").Should().Be(1,
                "loading is additive and deduplicates by name");
        });
    }

    // ---- helpers ----

    private static void RunInTempDir(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "revi-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            body(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Invokes the private <c>LoadFromFileSystem(string)</c>, unwrapping reflection exceptions.</summary>
    private static void InvokeLoadFromFileSystem(object service, string dir)
    {
        MethodInfo method = service.GetType()
            .GetMethod("LoadFromFileSystem", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("LoadFromFileSystem(string) not found");
        try
        {
            method.Invoke(service, new object[] { dir + Path.DirectorySeparatorChar });
        }
        catch (TargetInvocationException tie)
        {
            throw tie.InnerException ?? tie;
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="IReviLogger{T}"/> that records (level, message) pairs.
    /// Return values are unused by the loaders, so logging methods return <c>null!</c> to avoid any
    /// dependency on constructing a real <see cref="Rlog"/>.
    /// </summary>
    private sealed class RecordingReviLogger<T> : IReviLogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        private Rlog Record(LogLevel level, string message)
        {
            Entries.Add((level, message));
            return null!;
        }

        public Rlog LogInfo(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Info, message);
        public Rlog LogInfo(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Info, message);
        public Rlog LogDebug(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Debug, message);
        public Rlog LogDebug(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Debug, message);
        public Rlog LogWarning(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Warning, message);
        public Rlog LogWarning(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Warning, message);
        public Rlog LogError(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Error, message);
        public Rlog LogError(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Error, message);
        public Rlog LogFatal(string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Fatal, message);
        public Rlog LogFatal(Rlog parent, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(LogLevel.Fatal, message);
        public Rlog Log(Rlog? parent, LogLevel level, string message, string? identifier = "", int cycle = 0, string? tags = null, object? object1 = null, string? object1Name = null, object? object2 = null, string? object2Name = null, string? file = "", string? member = "", int? line = 0)
            => Record(level, message);

        public Task DumpLog(StringBuilder sb, string fileNamePrefix) => Task.CompletedTask;
        public Task DumpLog(string? textToDump, string fileNamePrefix, Rlog? record = null) => Task.CompletedTask;
        public Task DumpImage(byte[] imageBytes, string fileNamePrefix, string extension = "png") => Task.CompletedTask;
        public bool IsEnabled(LogLevel level) => true;
    }
}
