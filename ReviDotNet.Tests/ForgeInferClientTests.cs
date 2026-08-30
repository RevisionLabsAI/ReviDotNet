// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;
using Xunit;

namespace ReviDotNet.Tests;

/// <summary>
/// Pins the Forge request mapping — in particular that a caller's explicit model selection
/// reaches the wire. Before <c>ModelName</c> existed on the request, an explicit
/// <c>modelName</c> died at the client boundary and Forge routed by its own preferences,
/// silently changing behavior for callers that route explicitly.
/// </summary>
public class ForgeInferClientTests
{
    private static ForgeInferClient NewClient() => new(new ForgeInferConfig
    {
        ForgeUrl = "http://localhost:9",
        ApiKey = "test-key",
        ClientId = "test-client"
    });

    [Fact]
    public void BuildRequest_carries_the_explicit_model_name()
    {
        using var client = NewClient();
        var prompt = new Prompt { Name = "some-prompt", PreferredModels = ["model_a", "model_b"] };

        var request = client.BuildRequest(prompt, inputs: null, stream: true, modelName: "model_b");

        Assert.Equal("model_b", request.ModelName);
        Assert.Equal("some-prompt", request.PromptName);
        Assert.Equal(["model_a", "model_b"], request.PreferredModels);
        Assert.True(request.Stream);
    }

    [Fact]
    public void BuildRequest_leaves_model_name_null_when_the_caller_did_not_route_explicitly()
    {
        using var client = NewClient();

        var request = client.BuildRequest(new Prompt { Name = "some-prompt" }, inputs: null, stream: false);

        Assert.Null(request.ModelName);
        Assert.False(request.Stream);
    }
}
