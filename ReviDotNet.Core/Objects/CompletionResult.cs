// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Represents the response structure for inference requests.
/// </summary>
public class CompletionResult
{
    /// <summary>
    /// The entire prompt string for the inference request.
    /// </summary>
    public string FullPrompt { get; set; }
    
    /// <summary>
    /// List of output strings from the inference request.
    /// </summary>
    public List<string> Outputs { get; set; }

    /// <summary>
    /// The selected or main output string from the inference.
    /// </summary>
    public string Selected { get; set; }

    /// <summary>
    /// Reason the model finished generating the response.
    /// </summary>
    public string FinishReason { get; set; }

    /// <summary>
    /// Number of input (prompt) tokens as reported by the provider. Null if the provider did not return usage data.
    /// </summary>
    public int? InputTokens { get; set; }

    /// <summary>
    /// Number of output (completion) tokens as reported by the provider. Null if the provider did not return usage data.
    /// </summary>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// The model id the provider reported handling the request (Claude/OpenAI <c>model</c>,
    /// Gemini <c>modelVersion</c>). Null if the provider did not return it.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Native extended-thinking / reasoning text returned by the provider (Anthropic Claude
    /// <c>thinking</c> content blocks) when extended thinking is enabled via a model's
    /// <c>thinking-budget</c>. Null when the provider returned no reasoning. This is distinct from any
    /// "thinking" field a model may write into its own structured JSON output.
    /// </summary>
    public string? Thinking { get; set; }
}