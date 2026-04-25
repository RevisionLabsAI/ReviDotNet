// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Result of an AI-powered analysis of a prompt execution.
/// </summary>
public class AnalysisResult
{
    /// <summary>
    /// Whether the prompt response adequately fulfilled the request.
    /// </summary>
    public bool FulfilledRequest { get; set; }

    /// <summary>
    /// Quality score from 1 (poor) to 10 (perfect).
    /// </summary>
    public int QualityScore { get; set; }

    /// <summary>
    /// Detailed breakdown of how the prompt performed.
    /// </summary>
    public string Analysis { get; set; } = string.Empty;

    /// <summary>
    /// Specific suggestions for improving the prompt or its parameters.
    /// </summary>
    public string Improvements { get; set; } = string.Empty;
}
