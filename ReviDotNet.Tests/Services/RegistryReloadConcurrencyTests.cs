// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// The provider, model and embedding registries are read on every inference while a host may reload them. A reload
/// used to clear the live list and refill it, so a concurrent reader could find it empty or half-built, or throw while
/// enumerating it. These tests read from several threads while reloads and adds run, and pin the provider reload's
/// ordering: the old list stays visible until the new one is published, and only then are replaced providers disposed.
/// </summary>
public sealed class RegistryReloadConcurrencyTests
{
    /// <summary>Configs each reload reads.</summary>
    private const int ConfigCount = 6;

    /// <summary>Reloads (each followed by an add) the writer performs while readers run.</summary>
    private const int Reloads = 150;

    /// <summary>Concurrent reader threads.</summary>
    private const int ReaderCount = 4;

    [Fact]
    public async Task ProviderReadersNeverSeeAnEmptyOrPartialListDuringReloadsAndAdds()
    {
        ProviderManagerService service = new(new RecordingReviLogger<ProviderManagerService>());
        ConfigAssembly configs = new(Configs("Providers", i => SystemOneProvider($"provider-{i}")));
        await service.LoadAsync(configs);
        string[] names = [.. Enumerable.Range(0, ConfigCount).Select(i => $"provider-{i}")];
        service.GetAll().Select(p => p.Name).Should().Equal(names, "the embedded fallback serves the test configs");

        HammerResult result = await HammerAsync(
            async i =>
            {
                await service.LoadAsync(configs);
                service.Add(new ProviderProfile { Name = $"added-{i}", Enabled = true });
            },
            () =>
            {
                foreach (string name in names)
                    if (service.Get(name) is null) return $"Get(\"{name}\") returned null";
                List<ProviderProfile> all = service.GetAll();
                if (all.Count < ConfigCount) return $"GetAll() returned {all.Count} providers";
                return all.Any(p => p is null) ? "GetAll() returned a null entry" : null;
            });

        result.Failures.Should().BeEmpty("a reader must see either the old or the new complete list");
        result.Reads.Should().BeGreaterThan(0);
        service.Dispose();
    }

    [Fact]
    public async Task ModelReadersNeverSeeAnEmptyOrPartialListDuringReloadsAndAdds()
    {
        ModelManagerService service = new(ModelProviders(), new RecordingReviLogger<ModelManagerService>());
        ConfigAssembly configs = new(Configs("Models.Inference", i => Model($"model-{i}")));
        await service.LoadAsync(configs);
        string[] names = [.. Enumerable.Range(0, ConfigCount).Select(i => $"model-{i}")];
        service.GetAll().Select(m => m.Name).Should().Equal(names, "the embedded fallback serves the test configs");

        HammerResult result = await HammerAsync(
            async i =>
            {
                await service.LoadAsync(configs);
                service.Add(new ModelProfile { Name = $"added-{i}", Enabled = true });
            },
            () =>
            {
                foreach (string name in names)
                    if (service.Get(name) is null) return $"Get(\"{name}\") returned null";
                List<ModelProfile> all = service.GetAll();
                if (all.Count < ConfigCount) return $"GetAll() returned {all.Count} models";
                _ = service.Find(ModelTier.C);
                return all.Any(m => m is null) ? "GetAll() returned a null entry" : null;
            });

        result.Failures.Should().BeEmpty("a reader must see either the old or the new complete list");
        result.Reads.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EmbeddingReadersNeverSeeAnEmptyOrPartialListDuringReloadsAndAdds()
    {
        EmbeddingManagerService service = new(ModelProviders(), new RecordingReviLogger<EmbeddingManagerService>());
        ConfigAssembly configs = new(Configs("Models.Embedding", i => Model($"embedding-{i}")));
        await service.LoadAsync(configs);
        string[] names = [.. Enumerable.Range(0, ConfigCount).Select(i => $"embedding-{i}")];
        service.GetAll().Select(m => m.Name).Should().Equal(names, "the embedded fallback serves the test configs");

        HammerResult result = await HammerAsync(
            async i =>
            {
                await service.LoadAsync(configs);
                service.Add(new EmbeddingProfile { Name = $"added-{i}", Enabled = true });
            },
            () =>
            {
                foreach (string name in names)
                    if (service.Get(name) is null) return $"Get(\"{name}\") returned null";
                // GetAll is a read-only view; enumerating it must never observe a list being refilled.
                int count = service.GetAll().Count(m => m is not null);
                if (count < ConfigCount) return $"GetAll() enumerated {count} embedding models";
                int enabled = service.GetAllEnabled().Count;
                return enabled < ConfigCount ? $"GetAllEnabled() returned {enabled} embedding models" : null;
            });

        result.Failures.Should().BeEmpty("a reader must see either the old or the new complete list");
        result.Reads.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AProviderReloadKeepsTheOldListVisibleUntilItSwapsThenDisposesTheReplacedProviders()
    {
        ProviderManagerService service = new(new RecordingReviLogger<ProviderManagerService>());
        await service.LoadAsync(new ConfigAssembly(Configs("Providers", i => SystemOneProvider($"provider-{i}"))));
        List<ProviderProfile> old = service.GetAll();
        old.Should().HaveCount(ConfigCount).And.OnlyContain(p => p.DecisionClient != null, "a SystemOne provider owns its decision client");

        // Observed while the reload reads its configuration. Assertions cannot run in the callback: the loader
        // logs and swallows exceptions from a resource read.
        List<int> visibleCountDuringReload = [];
        List<bool> oldListVisibleDuringReload = [];
        List<bool> oldStillUsableDuringReload = [];
        ConfigAssembly next = new(Configs("Providers", i => SystemOneProvider($"provider-{i}")), () =>
        {
            List<ProviderProfile> visible = service.GetAll();
            visibleCountDuringReload.Add(visible.Count);
            oldListVisibleDuringReload.Add(visible.SequenceEqual(old));
            oldStillUsableDuringReload.Add(old.All(p => p.DecisionClient != null));
        });
        await service.LoadAsync(next);

        visibleCountDuringReload.Should().HaveCount(ConfigCount).And.OnlyContain(count => count == ConfigCount, "readers never see a half-built list");
        oldListVisibleDuringReload.Should().OnlyContain(same => same, "readers keep the complete old list until the swap");
        oldStillUsableDuringReload.Should().OnlyContain(usable => usable, "nothing is disposed before the new list is published");
        List<ProviderProfile> current = service.GetAll();
        current.Should().HaveCount(ConfigCount).And.OnlyContain(p => p.DecisionClient != null, "the published providers stay usable");
        current.Should().NotContain(p => old.Contains(p));
        old.Should().OnlyContain(p => p.DecisionClient == null, "replaced providers are disposed once the new list is published");
        service.Dispose();
    }

    [Fact]
    public void ConcurrentAddsAreNeverLost()
    {
        ProviderManagerService service = new(new RecordingReviLogger<ProviderManagerService>());
        Parallel.For(0, 2_000, i => service.Add(new ProviderProfile { Name = $"added-{i}", Enabled = true }));
        List<ProviderProfile> all = service.GetAll();
        all.Should().HaveCount(2_000).And.NotContainNulls();
        all.Select(p => p.Name).Should().OnlyHaveUniqueItems();
    }

    /// <summary>Outcome of one hammer run.</summary>
    /// <param name="Failures">What readers saw that they must not have (capped), including exceptions.</param>
    /// <param name="Reads">Completed reads, to prove the readers overlapped the writes.</param>
    private sealed record HammerResult(IReadOnlyCollection<string> Failures, long Reads);

    /// <summary>Runs <see cref="Reloads"/> writes on this thread while <see cref="ReaderCount"/> threads read continuously.</summary>
    /// <param name="write">One write step (a reload and an add), given its iteration number.</param>
    /// <param name="read">One read pass; returns a description of what was wrong, or null.</param>
    private static async Task<HammerResult> HammerAsync(Func<int, Task> write, Func<string?> read)
    {
        ConcurrentQueue<string> failures = new();
        long reads = 0;
        using CountdownEvent started = new(ReaderCount);
        using CancellationTokenSource stop = new();
        Task[] readers = [.. Enumerable.Range(0, ReaderCount).Select(_ => Task.Factory.StartNew(() =>
        {
            started.Signal();
            while (!stop.IsCancellationRequested && failures.Count < 50)
            {
                try
                {
                    string? failure = read();
                    if (failure is not null) failures.Enqueue(failure);
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"{ex.GetType().Name}: {ex.Message}");
                }
                Interlocked.Increment(ref reads);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))];

        started.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("every reader thread starts before the writes");
        try
        {
            for (int i = 0; i < Reloads; i++) await write(i);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(readers);
        }
        return new HammerResult([.. failures], Interlocked.Read(ref reads));
    }

    /// <summary><see cref="ConfigCount"/> embedded resources under <c>RConfigs.{folder}.</c>, the layout the loaders look for.</summary>
    /// <param name="folder">The resource folder segment, e.g. <c>Providers</c> or <c>Models.Inference</c>.</param>
    /// <param name="content">The <c>.rcfg</c> text for config <c>i</c>.</param>
    private static Dictionary<string, string> Configs(string folder, Func<int, string> content)
    {
        // LoadAsync reads BaseDirectory/RConfigs/<folder>/ and falls back to embedded resources only when it is missing.
        // (Other tests may leave an empty RConfigs root behind, which does not matter.)
        string loadPath = AppDomain.CurrentDomain.BaseDirectory + "RConfigs/" + folder.Replace('.', '/') + "/";
        Directory.Exists(loadPath).Should().BeFalse("LoadAsync must fall back to the embedded test configs, so {0} must not exist", loadPath);
        return Enumerable.Range(0, ConfigCount).ToDictionary(i => $"Revi.Tests.RConfigs.{folder}.config-{i}.rcfg", content);
    }

    /// <summary>A SystemOne provider: it owns a disposable decision client, which makes disposal observable.</summary>
    /// <param name="name">The provider name.</param>
    private static string SystemOneProvider(string name) =>
        $"[[general]]\nname = {name}\nenabled = true\nprotocol = SystemOne\napi-url = https://decisions.invalid\n";

    /// <summary>An inference or embedding model config on the enabled provider <see cref="ModelProviders"/> registers.</summary>
    /// <param name="name">The model name.</param>
    private static string Model(string name) =>
        $"[[general]]\nname = {name}\nenabled = true\nmodel-string = {name}-string\nprovider-name = model-provider\n";

    /// <summary>A provider registry holding the enabled provider the model configs name, so the models stay enabled.</summary>
    private static ProviderManagerService ModelProviders()
    {
        ProviderManagerService providers = new(new RecordingReviLogger<ProviderManagerService>());
        providers.Add(new ProviderProfile { Name = "model-provider", Enabled = true });
        return providers;
    }

    /// <summary>An assembly whose manifest resources are in-memory <c>.rcfg</c> texts; optionally reports each resource read.</summary>
    /// <param name="resources">Resource name to content.</param>
    /// <param name="onRead">Invoked before each resource stream is returned, i.e. while a reload is in progress.</param>
    private sealed class ConfigAssembly(IReadOnlyDictionary<string, string> resources, Action? onRead = null) : Assembly
    {
        /// <inheritdoc/>
        public override string[] GetManifestResourceNames() => [.. resources.Keys];

        /// <inheritdoc/>
        public override Stream? GetManifestResourceStream(string name)
        {
            onRead?.Invoke();
            return resources.TryGetValue(name, out string? content) ? new MemoryStream(Encoding.UTF8.GetBytes(content)) : null;
        }
    }
}
