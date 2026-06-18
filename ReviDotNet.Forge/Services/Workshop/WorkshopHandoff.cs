// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using ReviDotNet.Forge.Services.Workshop.Models;

namespace ReviDotNet.Forge.Services.Workshop;

/// <summary>
/// Per-circuit hand-off used to carry a one-shot request across a navigation. The "New Evaluation"
/// dialog (Evaluations list) and the "Evaluate these runs" action (Session viewer) both stash a
/// <see cref="NewEvaluationSpec"/> here and navigate to <c>/workshop/evaluation/new</c>, which the
/// EvaluationViewer page consumes to run the create-and-evaluate flow. Scoped so it lives for the
/// browser circuit and survives the navigation, then is cleared once read.
/// </summary>
public sealed class WorkshopHandoff
{
    public NewEvaluationSpec? PendingEvaluation { get; set; }

    /// <summary>Returns and clears the pending evaluation request (one-shot).</summary>
    public NewEvaluationSpec? TakeEvaluation()
    {
        var spec = PendingEvaluation;
        PendingEvaluation = null;
        return spec;
    }
}
