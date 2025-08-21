// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

namespace Revi;

/// <summary>
/// Represents the response structure for inference requests.
/// </summary>
public class CompletionResponse
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