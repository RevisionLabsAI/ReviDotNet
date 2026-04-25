// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Optimizer.Services;

/// <summary>
/// An example pair used during prompt generation.
/// </summary>
public class GeneratorExample
{
    public Dictionary<string, string> Inputs { get; set; } = new();
    public string Output { get; set; } = string.Empty;
}

/// <summary>
/// Uses AI to generate a .pmt prompt file from a description and examples.
/// </summary>
public class PromptGeneratorService
{
    /// <summary>
    /// Streams a generated .pmt file from the AI given a description and examples.
    /// Returns the full generated content.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string promptName,
        string purpose,
        List<GeneratorExample> examples,
        bool requestJson,
        string guidanceSchemaType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var examplesText = new System.Text.StringBuilder();
        for (int i = 0; i < examples.Count; i++)
        {
            var ex = examples[i];
            examplesText.AppendLine($"Example {i + 1}:");
            examplesText.AppendLine("  Inputs:");
            foreach (var kv in ex.Inputs)
                examplesText.AppendLine($"    {kv.Key}: {kv.Value}");
            examplesText.AppendLine($"  Expected Output: {ex.Output}");
            examplesText.AppendLine();
        }

        var inputs = new List<Input>
        {
            new("Prompt Name", promptName),
            new("Purpose", purpose),
            new("Examples", examplesText.ToString()),
            new("Request JSON", requestJson ? "true" : "false"),
            new("Guidance Schema Type", string.IsNullOrWhiteSpace(guidanceSchemaType) ? "none" : guidanceSchemaType)
        };

        Prompt? generatorPrompt = PromptManager.Get("Optimizer.Generator");
        if (generatorPrompt is null)
            throw new InvalidOperationException("Optimizer.Generator prompt not found. Ensure it exists in RConfigs/Prompts/Optimizer/.");

        await foreach (string token in Infer.CompletionStream(generatorPrompt, inputs).WithCancellation(ct))
            yield return token;
    }
}
