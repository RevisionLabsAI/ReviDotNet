// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Revi;
using Xunit;

namespace ReviDotNet.Tests.Clients;

/// <summary>
/// <see cref="ProviderProfile.InferenceHttpClientFactory"/> lets a host supply the HTTP client a profile's
/// inference client sends through, so a handler of the host's can see, and refuse, every model request.
/// <para>
/// The factory is process-wide and other test classes build profiles in parallel, so each test's factory
/// answers only for the uniquely named profile it created and restores the previous factory when done.
/// </para>
/// </summary>
public class InferenceHttpClientFactoryTests
{
    private const string OpenAiChatJson =
        "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    [Fact]
    public async Task EveryInitRoutesTheProfilesModelRequestsThroughTheHostsClient()
    {
        string name = "factory-" + Guid.NewGuid().ToString("N");
        CountingHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(OpenAiChatJson, Encoding.UTF8, "application/json")
        });
        List<string?> asked = [];
        using IDisposable installed = Install(profile =>
        {
            if (profile.Name != name) return null;
            asked.Add(profile.Name);
            return new HttpClient(handler, disposeHandler: false);
        });

        ProviderProfile provider = Profile(name, retryAttemptLimit: 0);
        await provider.InferenceClient!.GenerateAsync(new List<Message> { new("user", "hi") }, model: "test-model");
        handler.Calls.Should().Be(1, "the request went through the client the factory supplied");

        // Init is what a host calls after changing a profile's settings; the rebuilt client is hooked too.
        provider.Init();
        await provider.InferenceClient!.GenerateAsync(new List<Message> { new("user", "hi") }, model: "test-model");
        handler.Calls.Should().Be(2);
        asked.Should().Equal(name, name);
    }

    [Fact]
    public async Task AHandlersOwnExceptionIsNotRetriedAndReachesTheCaller()
    {
        string name = "factory-" + Guid.NewGuid().ToString("N");
        CountingHandler handler = new(() => throw new HostRefusedException());
        using IDisposable installed = Install(profile =>
            profile.Name == name ? new HttpClient(handler, disposeHandler: false) : null);

        ProviderProfile provider = Profile(name, retryAttemptLimit: 3);
        Func<Task> call = () => provider.InferenceClient!.GenerateAsync(
            new List<Message> { new("user", "hi") }, model: "test-model");

        await call.Should().ThrowAsync<HostRefusedException>();
        handler.Calls.Should().Be(1, "only transport failures and timeouts are retried");
    }

    [Fact]
    public void ANullResultOrNoFactoryKeepsTheBuiltInClient()
    {
        string name = "factory-" + Guid.NewGuid().ToString("N");
        using (Install(_ => null))
        {
            Profile(name, retryAttemptLimit: 0).InferenceClient.Should().NotBeNull();
        }

        Profile(name, retryAttemptLimit: 0).InferenceClient.Should().NotBeNull();
    }

    /// <summary>Builds an OpenAI-protocol profile, which runs <see cref="ProviderProfile.Init"/>.</summary>
    private static ProviderProfile Profile(string name, int retryAttemptLimit) => new(
        name: name,
        enabled: true,
        protocol: Protocol.OpenAI,
        apiURL: "http://factory.test/",
        apiKey: "not-a-key",
        timeoutSeconds: 30,
        retryAttemptLimit: retryAttemptLimit,
        retryInitialDelaySeconds: 0,
        simultaneousRequests: 2,
        defaultModel: "test-model",
        supportsCompletion: false,
        supportsGuidance: false);

    /// <summary>Installs a factory in front of whatever was installed, and restores that on dispose.</summary>
    private static IDisposable Install(Func<ProviderProfile, HttpClient?> factory)
    {
        Func<ProviderProfile, HttpClient?>? previous = ProviderProfile.InferenceHttpClientFactory;
        ProviderProfile.InferenceHttpClientFactory = profile => factory(profile) ?? previous?.Invoke(profile);
        return new Restore(previous);
    }

    private sealed class Restore(Func<ProviderProfile, HttpClient?>? previous) : IDisposable
    {
        public void Dispose() => ProviderProfile.InferenceHttpClientFactory = previous;
    }

    private sealed class HostRefusedException : Exception;

    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond());
        }
    }
}
