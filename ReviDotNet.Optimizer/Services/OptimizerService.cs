// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;

namespace ReviDotNet.Optimizer.Services;

/// <summary>
/// An AI-generated suggestion for improving a prompt.
/// </summary>
public class PromptSuggestion
{
    public required string Description { get; init; }
    public required string ExpectedImpact { get; init; }
    public required string AffectedSection { get; init; }
    public bool Selected { get; set; } = true;
}

/// <summary>
/// Provides AI-powered prompt analysis, suggestion aggregation, and revision.
/// </summary>
public class OptimizerService
{
    /// <summary>
    /// Analyzes a single prompt execution result using the Optimizer.Analyzer prompt.
    /// </summary>
    public async Task<AnalysisResult?> AnalyzeAsync(
        string promptName,
        string modelName,
        List<Input> inputs,
        string response)
    {
        var analysisInputs = new List<Input>
        {
            new("Prompt Name", promptName),
            new("Model", modelName),
            new("Inputs", string.Join(", ", inputs.Select(i => $"{i.Label}={i.Text}"))),
            new("Response", response)
        };

        return await Infer.ToObject<AnalysisResult>("Optimizer.Analyzer", analysisInputs);
    }

    /// <summary>
    /// Aggregates multiple analysis results into a ranked list of concrete suggestions
    /// using the Optimizer.Suggester prompt.
    /// </summary>
    public async Task<List<PromptSuggestion>> GenerateSuggestionsAsync(
        Prompt originalPrompt,
        List<AnalysisResult> analyses)
    {
        if (!analyses.Any())
            return [];

        var aggregatedAnalyses = new System.Text.StringBuilder();
        for (int i = 0; i < analyses.Count; i++)
        {
            var a = analyses[i];
            aggregatedAnalyses.AppendLine($"Result {i + 1}:");
            aggregatedAnalyses.AppendLine($"  Fulfilled: {a.FulfilledRequest}");
            aggregatedAnalyses.AppendLine($"  Quality: {a.QualityScore}/10");
            aggregatedAnalyses.AppendLine($"  Analysis: {a.Analysis}");
            aggregatedAnalyses.AppendLine($"  Improvements: {a.Improvements}");
            aggregatedAnalyses.AppendLine();
        }

        var inputs = new List<Input>
        {
            new("Prompt Name", originalPrompt.Name ?? "Unknown"),
            new("Current System", originalPrompt.System ?? string.Empty),
            new("Current Instruction", originalPrompt.Instruction ?? string.Empty),
            new("Analysis Results", aggregatedAnalyses.ToString())
        };

        var result = await Infer.ToObject<SuggesterResult>("Optimizer.Suggester", inputs);
        return result?.Suggestions ?? [];
    }

    /// <summary>
    /// Generates a revised prompt using the Optimizer.Reviser prompt.
    /// Returns the full .pmt file content as a string streamed token by token.
    /// </summary>
    public async IAsyncEnumerable<string> ReviseStreamAsync(
        Prompt originalPrompt,
        List<PromptSuggestion> selectedSuggestions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var suggestionsText = string.Join("\n", selectedSuggestions
            .Where(s => s.Selected)
            .Select((s, i) => $"{i + 1}. [{s.AffectedSection}] {s.Description} — Impact: {s.ExpectedImpact}"));

        var inputs = new List<Input>
        {
            new("Prompt Name", originalPrompt.Name ?? "Unknown"),
            new("Current System", originalPrompt.System ?? string.Empty),
            new("Current Instruction", originalPrompt.Instruction ?? string.Empty),
            new("Selected Suggestions", suggestionsText)
        };

        Prompt? reviserPrompt = PromptManager.Get("Optimizer.Reviser");
        if (reviserPrompt is null)
            throw new InvalidOperationException("Optimizer.Reviser prompt not found.");

        await foreach (string token in Infer.CompletionStream(reviserPrompt, inputs).WithCancellation(ct))
            yield return token;
    }

    // Internal type for deserializing suggester output
    private class SuggesterResult
    {
        public List<PromptSuggestion> Suggestions { get; set; } = [];
    }
}
