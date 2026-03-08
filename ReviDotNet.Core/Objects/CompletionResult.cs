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
}