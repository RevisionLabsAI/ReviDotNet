// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Revi;
using ReviDotNet.Forge.Services.Workshop;
using ReviDotNet.Forge.Services.Workshop.Models;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// The result of running one whole suite (agent- or prompt-mode) with assertions.
/// </summary>
public sealed record SuiteRunSummary(
    string SuiteName,
    string Mode,
    int Total,
    int Passed,
    IReadOnlyList<SuiteCaseResult> Cases);

/// <summary>
/// The result of one suite input case: the produced output and the per-assertion outcomes.
/// <see cref="Passed"/> is true when every assertion passed (a case with no assertions counts as passed).
/// </summary>
public sealed record SuiteCaseResult(
    int Index,
    bool Passed,
    string? Output,
    IReadOnlyList<AssertionResult> Assertions);

/// <summary>
/// Runs a <see cref="SavedSuite"/> in either agent-mode or prompt-mode and evaluates its assertions.
/// Agent-mode (the suite's <see cref="SavedSuite.AgentName"/> or an <c>agentOverride</c> is set) drives one
/// sample via <see cref="IAgentWorkshopService.RunMultiAsync"/> and asserts on the final output. Prompt-mode
/// falls back to the existing <see cref="TestRunnerService"/> single-prompt path. Assertions are evaluated by
/// <see cref="SuiteAssertionEvaluator"/>, with this service's <see cref="IInferService"/> as the ScoreMin judge.
/// </summary>
public sealed class AgentTestRunnerService
{
    private readonly IAgentWorkshopService _workshop;
    private readonly TestRunnerService? _prompts;
    private readonly IInferService _infer;

    /// <summary>Initialises the agent-aware test runner. <paramref name="prompts"/> may be null (agent-only host).</summary>
    public AgentTestRunnerService(IAgentWorkshopService workshop, TestRunnerService? prompts, IInferService infer)
    {
        _workshop = workshop;
        _prompts = prompts;
        _infer = infer;
    }

    /// <summary>
    /// Runs every input case of <paramref name="suite"/> and evaluates its assertions. When the suite (or
    /// <paramref name="agentOverride"/>) names an agent, each case runs through the agent; otherwise each case
    /// runs the suite's prompt. Returns a summary with per-case results and the overall pass count.
    /// </summary>
    public async Task<SuiteRunSummary> RunSuiteAsync(SavedSuite suite, string? agentOverride, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suite);

        string? agentName = !string.IsNullOrWhiteSpace(agentOverride) ? agentOverride
            : !string.IsNullOrWhiteSpace(suite.AgentName) ? suite.AgentName
            : null;
        bool agentMode = agentName is not null;
        string mode = agentMode ? "agent" : "prompt";

        IReadOnlyList<SuiteAssertion> assertions =
            suite.Assertions is { Count: > 0 } a ? a : Array.Empty<SuiteAssertion>();

        var cases = new List<SuiteCaseResult>(suite.Inputs.Count);
        for (int i = 0; i < suite.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string output = agentMode
                ? await RunAgentCaseAsync(agentName!, suite.Inputs[i], ct).ConfigureAwait(false)
                : await RunPromptCaseAsync(suite, suite.Inputs[i], ct).ConfigureAwait(false);

            IReadOnlyList<AssertionResult> caseAssertions =
                await SuiteAssertionEvaluator.EvaluateAsync(output, assertions, _infer, ct).ConfigureAwait(false);

            bool passed = caseAssertions.All(r => r.Passed); // empty => true
            cases.Add(new SuiteCaseResult(i, passed, output, caseAssertions));
        }

        int passedCount = cases.Count(c => c.Passed);
        return new SuiteRunSummary(suite.Name, mode, cases.Count, passedCount, cases);
    }

    // ── Agent-mode ───────────────────────────────────────────────────────

    private async Task<string> RunAgentCaseAsync(string agentName, SavedSuiteInput input, CancellationToken ct)
    {
        var request = new WorkshopRunRequest
        {
            AgentName = agentName,
            Task = input.Value,
            Runs = 1,
            AdditionalInputs = string.IsNullOrWhiteSpace(input.Key)
                ? null
                : new Dictionary<string, object> { [input.Key] = input.Value }
        };

        string? finalOutput = null;
        await foreach (WorkshopRunUpdate update in _workshop.RunMultiAsync(request, ct).ConfigureAwait(false))
        {
            if (update.FinalResult is { } result)
                finalOutput = result.FinalOutput;
        }

        return finalOutput ?? string.Empty;
    }

    // ── Prompt-mode ──────────────────────────────────────────────────────

    private async Task<string> RunPromptCaseAsync(SavedSuite suite, SavedSuiteInput input, CancellationToken ct)
    {
        if (_prompts is null)
            throw new InvalidOperationException(
                "Prompt-mode suite run requires TestRunnerService, but none is registered.");

        string? modelName = suite.ModelNames.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(modelName))
            throw new InvalidOperationException($"Suite '{suite.Name}' has no model to run the prompt against.");

        var inputs = new List<Input> { new(input.Key, input.Value) };

        var channel = _prompts.RunTests(
            suite.PromptName,
            new[] { modelName },
            inputs,
            runsPerModel: 1,
            runAnalysis: false,
            ct);

        await foreach (TestRunResult result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (result.Success)
                return result.Output;
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                throw new InvalidOperationException($"Prompt run failed: {result.ErrorMessage}");
        }

        return string.Empty;
    }
}
