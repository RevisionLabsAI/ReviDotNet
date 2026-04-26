// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Revi;

public class ForgeInferClient : IDisposable
{
    private readonly ForgeInferConfig _config;
    private readonly HttpClient _http;

    public ForgeInferClient(ForgeInferConfig config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.ForgeUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300)
        };
        _http.DefaultRequestHeaders.Add("X-Forge-ApiKey", config.ApiKey);
    }

    public async Task<CompletionResult?> GenerateAsync(
        Prompt prompt,
        List<Input>? inputs,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(prompt, inputs, stream: false);
        try
        {
            using var response = await _http.PostAsJsonAsync("api/v1/infer", request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var forgeResponse = await response.Content.ReadFromJsonAsync<ForgeInferResponse>(
                cancellationToken: cancellationToken);
            if (forgeResponse is null || !forgeResponse.Success) return null;

            var output = forgeResponse.Output ?? string.Empty;
            return new CompletionResult
            {
                Selected = output,
                Outputs = [output],
                FullPrompt = string.Empty,
                FinishReason = "stop"
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        Prompt prompt,
        List<Input>? inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(prompt, inputs, stream: true);
        HttpResponseMessage? response = null;
        try
        {
            response = await _http.PostAsJsonAsync("api/v1/infer", request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            yield break;
        }
        catch
        {
            response?.Dispose();
            yield break;
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            yield break;
        }

        using (response)
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(stream);

            string? eventType = null;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (line.StartsWith("event: "))
                {
                    eventType = line["event: ".Length..].Trim();
                }
                else if (line.StartsWith("data: "))
                {
                    var data = line["data: ".Length..];
                    if (eventType == "chunk")
                    {
                        string? text = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            if (doc.RootElement.TryGetProperty("text", out var textEl))
                                text = textEl.GetString();
                        }
                        catch { }

                        if (text is not null)
                            yield return text;
                    }
                    else if (eventType == "done" || eventType == "error")
                    {
                        yield break;
                    }
                    eventType = null;
                }
            }
        }
    }

    private ForgeInferRequest BuildRequest(Prompt prompt, List<Input>? inputs, bool stream) =>
        new ForgeInferRequest
        {
            ClientId = _config.ClientId,
            PromptName = prompt.Name,
            Inputs = inputs?.Select(i => new ForgeInput(i.Label, i.Text)).ToList(),
            MinTier = Enum.TryParse<ModelTier>(prompt.MinTier, out var tier) ? tier : null,
            PreferredModels = prompt.PreferredModels,
            BlockedModels = prompt.BlockedModels,
            CompletionType = Enum.TryParse<CompletionType>(prompt.CompletionType, out var ct) ? ct : null,
            Temperature = prompt.Temperature,
            Stream = stream
        };

    public void Dispose() => _http.Dispose();
}

// Minimal DTOs mirroring what Forge sends/receives — avoids a project reference to Forge.
internal record ForgeInferRequest
{
    public required string ClientId { get; init; }
    public string? PromptName { get; init; }
    public List<ForgeInput>? Inputs { get; init; }
    public ModelTier? MinTier { get; init; }
    public List<string>? PreferredModels { get; init; }
    public List<string>? BlockedModels { get; init; }
    public CompletionType? CompletionType { get; init; }
    public float? Temperature { get; init; }
    public bool Stream { get; init; } = true;
}

internal record ForgeInput(string Label, string Text);

internal record ForgeInferResponse
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
}
