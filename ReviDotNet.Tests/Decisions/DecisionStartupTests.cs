using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Revi;
using Revi.Refinery;
using Xunit;

namespace ReviDotNet.Tests.Decisions;

/// <summary>Exercises real named configs and dependency-ordered startup without making provider requests.</summary>
public sealed class DecisionStartupTests
{
    [Fact]
    public async Task StartupKeepsApplicationAndPluginPromptsAndSeparatesDecisionModels()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "DecisionExamples", "RConfigs");
        ServiceCollection services = new();
        services.AddReviDotNet(typeof(DecisionStartupTests).Assembly, options =>
        {
            options.AdditionalAssemblies.Add(typeof(LlmJudge).Assembly);
            options.AdditionalConfigDirectories.Add(root);
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService startup = provider.GetServices<IHostedService>().Single();
        await startup.StartAsync(CancellationToken.None);
        IPromptManager prompts = provider.GetRequiredService<IPromptManager>();
        prompts.Get("decision-app-fixture").Should().NotBeNull();
        prompts.Get(LlmJudge.JudgePromptName).Should().NotBeNull();
        IDecisionRegistry decisions = provider.GetRequiredService<IDecisionRegistry>();
        decisions.GetPrompt("support-triage").Questions.Should().HaveCount(3);
        decisions.GetModel("decision-default").InputPerMillion.Should().Be(.042m);
        ProviderProfile transport = provider.GetRequiredService<IProviderManager>().Get("typesafe")!;
        transport.Protocol.Should().Be(Protocol.SystemOne);
        transport.SimultaneousRequests.Should().Be(8);
        transport.DecisionClient.Should().NotBeNull(); transport.InferenceClient.Should().BeNull(); transport.EmbeddingClient.Should().BeNull();
        provider.GetRequiredService<IModelManager>().Get("decision-default").Should().BeNull();
        prompts.AddOrUpdate(new Prompt { Name = "temporary", Version = 1 });
        await startup.StartAsync(CancellationToken.None);
        prompts.Get("temporary").Should().BeNull();
        prompts.Get("decision-app-fixture").Should().NotBeNull();
        prompts.Get(LlmJudge.JudgePromptName).Should().NotBeNull();
    }

    [Fact]
    public async Task StartupSkipsExtraAssembliesForHostRegistriesWithoutAdditiveLoading()
    {
        ServiceCollection services = new();
        services.AddReviDotNet(typeof(DecisionStartupTests).Assembly, options => options.AdditionalAssemblies.Add(typeof(LlmJudge).Assembly));
        services.AddSingleton<IToolManager, LegacyToolManager>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService startup = provider.GetServices<IHostedService>().Single();
        Func<Task> start = () => startup.StartAsync(CancellationToken.None);
        await start.Should().NotThrowAsync();
        provider.GetRequiredService<IPromptManager>().Get(LlmJudge.JudgePromptName).Should().NotBeNull();
        provider.GetRequiredService<IDecisionRegistry>().GetPrompts().Should().NotBeEmpty("registries after the tool registry still load");
    }

    /// <summary>A host tool registry written before additive loading: it keeps the interface's default LoadAssembly.</summary>
    private sealed class LegacyToolManager : IToolManager
    {
        public Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void LoadDirectory(string rootDirectory) { }
        public void Register(IBuiltInTool tool) { }
        public bool Unregister(string name) => false;
        public IBuiltInTool? GetBuiltIn(string name) => null;
        public IReadOnlyCollection<string> GetBuiltInNames() => [];
        public ToolProfile? GetCustom(string name) => null;
        public List<ToolProfile> GetAllCustom() => [];
    }

    [Fact]
    public async Task StandaloneBuilderNeedsNoHostSpecificLoggerRegistration()
    {
        await using ReviClient client = await ReviBuilder.Create().WithAssembly(typeof(DecisionStartupTests).Assembly).BuildAsync();
        client.Decide.Should().NotBeNull(); client.Documents.Should().NotBeNull();
    }
}
