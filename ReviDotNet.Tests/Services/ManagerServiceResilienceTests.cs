// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// Locks in the "one malformed config file doesn't abort the whole batch" resilience behavior shared
/// by the registry manager services. Each <c>LoadFromFileSystem</c> reads its <c>.rcfg</c> files in a
/// per-file try/catch (see <see cref="ProviderManagerService"/>, <see cref="ModelManagerService"/>,
/// <see cref="EmbeddingManagerService"/>, and the established pattern in <c>PromptManagerService</c>):
/// a single bad file must be logged and skipped while the valid files still load.
///
/// <para>
/// <c>LoadFromFileSystem</c> is private and builds its path from
/// <c>AppDomain.CurrentDomain.BaseDirectory</c>, so to keep the test hermetic we point it at a fresh
/// temp directory by invoking it via reflection. This avoids depending on (or mutating) whatever
/// <c>RConfigs/</c> tree happens to be next to the test assembly.
/// </para>
/// </summary>
public class ManagerServiceResilienceTests
{
    [Fact]
    public void ProviderManager_MalformedFile_IsSkippedAndLogged_ValidStillLoads()
    {
        RecordingReviLogger<ProviderManagerService> logger = new();
        ProviderManagerService service = new(logger);

        using TempConfigDir configs = new(
            valid:
                """
                [[general]]
                name = valid-provider
                enabled = true
                protocol = OpenAI
                api-url = https://example.test/v1/
                default-model = test-model
                """,
            // 'enabled' must be a bool; 'notabool' fails type conversion in RConfigParser.ToObject.
            malformed:
                """
                [[general]]
                name = malformed-provider
                enabled = notabool
                api-url = https://example.test/v1/
                """);

        InvokeLoadFromFileSystem(service, configs.LoadPath);

        service.GetAll().Should().ContainSingle("the valid provider loads even though a sibling file is malformed")
            .Which.Name.Should().Be("valid-provider");
        service.GetAll().Should().NotContain(p => p.Name == "malformed-provider");
        AssertMalformedFileLogged(logger);
    }

    [Fact]
    public void ModelManager_MalformedFile_IsSkippedAndLogged_ValidStillLoads()
    {
        RecordingReviLogger<ModelManagerService> logger = new();
        ModelManagerService service = new(EmptyProviderRegistry(), logger);

        using TempConfigDir configs = new(
            valid:
                """
                [[general]]
                name = valid-model
                enabled = true
                model-string = test-model-string
                provider-name = some-provider
                """,
            malformed:
                """
                [[general]]
                name = malformed-model
                enabled = notabool
                provider-name = some-provider
                """);

        InvokeLoadFromFileSystem(service, configs.LoadPath);

        service.GetAll().Should().ContainSingle("the valid model loads even though a sibling file is malformed")
            .Which.Name.Should().Be("valid-model");
        service.GetAll().Should().NotContain(m => m.Name == "malformed-model");
        AssertMalformedFileLogged(logger);
    }

    [Fact]
    public void EmbeddingManager_MalformedFile_IsSkippedAndLogged_ValidStillLoads()
    {
        RecordingReviLogger<EmbeddingManagerService> logger = new();
        EmbeddingManagerService service = new(EmptyProviderRegistry(), logger);

        using TempConfigDir configs = new(
            valid:
                """
                [[general]]
                name = valid-embedding
                enabled = true
                model-string = test-embedding-string
                provider-name = some-provider
                """,
            malformed:
                """
                [[general]]
                name = malformed-embedding
                enabled = notabool
                provider-name = some-provider
                """);

        InvokeLoadFromFileSystem(service, configs.LoadPath);

        service.GetAll().Should().ContainSingle("the valid embedding model loads even though a sibling file is malformed")
            .Which.Name.Should().Be("valid-embedding");
        service.GetAll().Should().NotContain(m => m.Name == "malformed-embedding");
        AssertMalformedFileLogged(logger);
    }

    /// <summary>An empty provider registry for the Model/Embedding services to resolve against.</summary>
    private static IProviderManager EmptyProviderRegistry()
        => new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>());

    /// <summary>
    /// Invokes the private <c>LoadFromFileSystem(string)</c> against <paramref name="path"/> and
    /// asserts it does not throw — the whole point of the per-file try/catch is that a bad file is
    /// swallowed rather than aborting the batch.
    /// </summary>
    private static void InvokeLoadFromFileSystem(object service, string path)
    {
        MethodInfo? method = service.GetType().GetMethod(
            "LoadFromFileSystem",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull(
            "the resilient per-file loader is invoked via reflection — update this test if it is renamed");

        // method.Invoke wraps any exception thrown by the loader in TargetInvocationException, so
        // NotThrow() here precisely asserts the loader swallowed the malformed file.
        Action invoke = () => method!.Invoke(service, new object[] { path });
        invoke.Should().NotThrow("a malformed config file must be skipped, not abort loading the rest");
    }

    /// <summary>Asserts the malformed file was reported by name at error level.</summary>
    private static void AssertMalformedFileLogged(RecordingReviLogger logger)
        => logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Error && e.Message.Contains(TempConfigDir.MalformedFileName),
            "the skipped file must be surfaced by name so the failure is diagnosable");

    /// <summary>
    /// Creates a throwaway directory containing exactly one valid and one deliberately malformed
    /// <c>.rcfg</c> file, and deletes it on dispose.
    /// </summary>
    private sealed class TempConfigDir : IDisposable
    {
        public const string ValidFileName = "valid.rcfg";
        public const string MalformedFileName = "malformed.rcfg";

        private readonly string _root;

        /// <summary>Path passed to the loader, with a trailing separator to mirror the production path.</summary>
        public string LoadPath => _root + Path.DirectorySeparatorChar;

        public TempConfigDir(string valid, string malformed)
        {
            _root = Path.Combine(Path.GetTempPath(), "revi-loader-resilience-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, ValidFileName), valid);
            File.WriteAllText(Path.Combine(_root, MalformedFileName), malformed);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; never fail a test over a leftover temp directory.
            }
        }
    }
}
