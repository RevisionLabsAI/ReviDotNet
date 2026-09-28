// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using FluentAssertions;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Services;

/// <summary>
/// A tier given as a string must parse case-insensitively when selecting an embedding model, as it already does for
/// inference models (<see cref="ModelManagerService"/>). A lowercase "a" or "b" used to fail to parse and silently
/// fall back to tier C, so the cheapest model was chosen whatever minimum the caller asked for.
/// </summary>
public sealed class EmbeddingTierParsingTests
{
    [Theory]
    [InlineData("a", ModelTier.A)]
    [InlineData("A", ModelTier.A)]
    [InlineData("b", ModelTier.B)]
    [InlineData("c", ModelTier.C)]
    public void ServiceFindParsesTheMinimumTierCaseInsensitively(string minTier, ModelTier expected)
    {
        EmbeddingManagerService service = new(new ProviderManagerService(new RecordingReviLogger<ProviderManagerService>()),
            new RecordingReviLogger<EmbeddingManagerService>());
        service.Add(new EmbeddingProfile { Name = "embed-a", Enabled = true, Tier = ModelTier.A });
        service.Add(new EmbeddingProfile { Name = "embed-b", Enabled = true, Tier = ModelTier.B });
        service.Add(new EmbeddingProfile { Name = "embed-c", Enabled = true, Tier = ModelTier.C });

        service.Find(minTier)!.Tier.Should().Be(expected);
        service.Find(minTier, blockedModels: ["unrelated"])!.Tier.Should().Be(expected);
    }

    [Theory]
    [InlineData("a", ModelTier.A)]
    [InlineData("b", ModelTier.B)]
    public void StaticFindParsesTheMinimumTierCaseInsensitively(string minTier, ModelTier expected)
    {
        // The static registry is process-wide; unique names keep this test's entries apart from others'.
        string suffix = Guid.NewGuid().ToString("n")[..8];
        EmbeddingManager.Add(new EmbeddingProfile { Name = $"embed-a-{suffix}", Enabled = true, Tier = ModelTier.A });
        EmbeddingManager.Add(new EmbeddingProfile { Name = $"embed-b-{suffix}", Enabled = true, Tier = ModelTier.B });
        EmbeddingManager.Add(new EmbeddingProfile { Name = $"embed-c-{suffix}", Enabled = true, Tier = ModelTier.C });

        EmbeddingManager.Find(minTier)!.Tier.Should().Be(expected);
        EmbeddingManager.Find(minTier, blockedModels: ["unrelated"])!.Tier.Should().Be(expected);
    }
}
