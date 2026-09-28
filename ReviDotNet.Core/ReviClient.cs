// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.DependencyInjection;

namespace Revi;

/// <summary>
/// Provides access to all primary ReviDotNet services when using the standalone builder path
/// (<see cref="Revi.CreateBuilder"/>). Dispose to release the underlying service provider.
/// </summary>
/// <remarks>
/// In host-based applications, inject <see cref="IInferService"/>, <see cref="IAgentService"/>,
/// and <see cref="IEmbedService"/> directly instead of using <see cref="ReviClient"/>.
/// </remarks>
public sealed class ReviClient : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    internal ReviClient(ServiceProvider provider)
    {
        _provider = provider;
        Infer = provider.GetRequiredService<IInferService>();
        Agent = provider.GetRequiredService<IAgentService>();
        Embed = provider.GetRequiredService<IEmbedService>();
        Decide = provider.GetRequiredService<IDecisionService>();
        Documents = provider.GetRequiredService<IDocumentSearchService>();
    }

    /// <summary>LLM inference service. Call as <c>revi.Infer.ToObject&lt;T&gt;(...)</c>.</summary>
    public IInferService Infer { get; }

    /// <summary>Agent execution service. Call as <c>revi.Agent.Run(...)</c>.</summary>
    public IAgentService Agent { get; }

    /// <summary>Embedding service. Call as <c>revi.Embed.Generate(...)</c>.</summary>
    public IEmbedService Embed { get; }

    /// <summary>Typed decision service using named question packs and model profiles.</summary>
    public IDecisionService Decide { get; }

    /// <summary>Search over explicit authorized document collections.</summary>
    public IDocumentSearchService Documents { get; }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
