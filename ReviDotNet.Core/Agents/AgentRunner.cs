// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Revi;

/// <summary>
/// Executes an agent loop defined by an AgentProfile.
/// Manages state transitions, guardrails, parallel tool execution, loop detection,
/// and emits a structured ReviLog event tree for the run lifecycle.
/// </summary>
public class AgentRunner
{
    /// <summary>Default maximum sub-agent nesting depth when no guardrail override is supplied.</summary>
    public const int DefaultMaxAgentDepth = 3;

    /// <summary>How many invalid-signal corrections to absorb per state activation before terminating.</summary>
    public const int MaxSignalCorrectionsPerActivation = 2;

    /// <summary>Conservative output-token estimate used in cost projection when a model has no max-tokens configured.</summary>
    public const int DefaultProjectedOutputTokens = 4096;

    // ==============
    //  State
    // ==============

    private readonly AgentProfile _profile;
    private readonly Dictionary<string, object> _inputs;
    private readonly CancellationToken _token;
    private readonly AgentRunContext _ctx;
    private readonly IModelManager _models;
    private readonly IPromptManager _prompts;
    private readonly IToolManager _tools;

    /// <summary>
    /// Optional pre-seeded conversation for an interactive chat turn. When supplied, the run starts
    /// from this conversation (prior turns plus the new user message) instead of synthesising an
    /// initial user message from <see cref="_inputs"/>. Null/empty for a normal fixed run.
    /// </summary>
    private readonly IReadOnlyList<Message>? _seedHistory;

    /// <summary>
    /// Optional per-run model override. When non-null, this model is used for every LLM call instead
    /// of resolving per-state from the model registry. Used by the per-run isolation path so a
    /// candidate run can pin its own model without mutating any shared registry.
    /// </summary>
    private readonly ModelProfile? _modelOverride;

    /// <summary>Unique id for this agent activation. Tagged on every emitted log event.</summary>
    public string SessionId { get; }

    /// <summary>The run-root Rlog. All step events for this run are children of it. Emitted at the
    /// start of <see cref="RunAsync"/> (not in the constructor) so a consumer that subscribes to the
    /// event bus using <see cref="SessionId"/> after construction still captures this root event.</summary>
    private Rlog _runRoot = null!;

    private string _currentStateName = "";
    private AgentState _currentState = null!;

    // Per-activation counters (reset on state transition)
    private int _currentStateCycles;   // number of times the current state has been activated
    private int _currentStateSteps;    // LLM calls in the current activation
    private int _currentStateToolCalls; // tool calls dispatched in the current activation
    private int _signalsCorrectedThisActivation; // unknown-signal nudges sent in the current activation
    private int _malformedStepsThisActivation;   // malformed-JSON reformat nudges sent in the current activation
    private decimal _currentStateCost; // cumulative USD cost of LLM calls in the current activation
    private bool _currentStateBudgetWarned; // whether the 80% warning has been emitted for this activation
    private DateTime _stateActivatedAt;

    // Run-wide tracking
    private readonly List<Message> _conversationHistory = new();
    private readonly List<string> _stateTraversalHistory = new();
    private int _totalSteps;
    private decimal _runTotalCost; // run-wide cumulative USD cost across all states
    private bool _runBudgetWarned; // whether the run-wide 80% warning has been emitted
    private string? _lastContent;


    // ==============
    //  Constructor
    // ==============

    /// <summary>Creates an <see cref="AgentRunner"/> using the injected service managers (preferred path).</summary>
    public AgentRunner(AgentProfile profile, Dictionary<string, object> inputs, CancellationToken token,
        AgentRunContext ctx, IModelManager models, IPromptManager prompts, IToolManager tools,
        IReadOnlyList<Message>? seedHistory = null, ModelProfile? modelOverride = null)
    {
        _profile = profile;
        _inputs = inputs;
        _token = token;
        _ctx = ctx;
        _models = models;
        _prompts = prompts;
        _tools = tools;
        _seedHistory = seedHistory;
        _modelOverride = modelOverride;

        SessionId = Guid.NewGuid().ToString("n");
        // NOTE: the run-root event is emitted at the start of RunAsync, not here — see _runRoot.
    }


    // ==============
    //  Main Loop
    // ==============

    public async Task<AgentResult> RunAsync()
    {
        // Emit the run-root event now (not in the constructor) so a consumer that subscribed to the
        // event bus by SessionId between construction and this call still receives it — otherwise the
        // live trace/grouped view would miss its root and render blank.
        _runRoot = AgentReviLogger.LogStart(
            parentLog: _ctx.ParentLog,
            agentName: _profile.Name ?? "(unnamed)",
            sessionId: SessionId,
            depth: _ctx.Depth,
            entryState: _profile.EntryState ?? "",
            inputs: _inputs,
            profileSummary: BuildProfileSummary());

        // Transition into the entry state (no state-transition event for the seed entry)
        TransitionToState(_profile.EntryState!, isEntry: true);

        // Seed the conversation: a chat turn supplies the full conversation (prior turns + the new
        // user message); a fixed run synthesises one initial user message from the agent's inputs.
        if (_seedHistory is { Count: > 0 })
            _conversationHistory.AddRange(_seedHistory);
        else
            _conversationHistory.Add(new Message("user", BuildInitialUserMessage()));

        while (true)
        {
            _token.ThrowIfCancellationRequested();

            // ── Guardrail check ──────────────────────────────────────────────
            var (violated, violationMsg) = CheckGuardrails();
            if (violated)
            {
                Util.Log($"AgentRunner '{_profile.Name}': Guardrail violated in state '{_currentStateName}': {violationMsg}");
                LogStep(AgentReviLogger.Step.GuardrailViolation, $"Guardrail violated: {violationMsg}", level: LogLevel.Warning);
                return Terminate(AgentExitReason.GuardrailViolation, guardrailMessage: violationMsg);
            }

            // ── Budget check (graceful: refuses the next call rather than mid-call) ──
            var (overBudget, budgetMsg) = CheckBudget();
            if (overBudget)
            {
                Util.Log($"AgentRunner '{_profile.Name}': Budget exceeded in state '{_currentStateName}': {budgetMsg}");
                LogStep(AgentReviLogger.Step.GuardrailViolation, $"Budget exceeded: {budgetMsg}", level: LogLevel.Warning);
                return Terminate(AgentExitReason.BudgetExceeded, guardrailMessage: budgetMsg);
            }

            // ── Loop detection check ─────────────────────────────────────────
            if (_currentState.Guardrails.LoopDetection == true && DetectLoop())
            {
                Util.Log($"AgentRunner '{_profile.Name}': Loop detected in state traversal history.");
                LogStep(AgentReviLogger.Step.GuardrailViolation, "Loop detected in state traversal history", level: LogLevel.Warning);
                return Terminate(AgentExitReason.LoopDetected);
            }

            // ── Build messages and call LLM ──────────────────────────────────
            List<Message> messages = BuildStepMessages();

            CompletionResult? llmResult = null;
            Rlog requestLog = LogStep(
                AgentReviLogger.Step.LlmRequest,
                $"LLM request (step {_totalSteps + 1})",
                object1: messages,
                object1Name: "messages");

            // Retry a failed LLM call up to the state's retry-limit guardrail before giving up.
            int retryLimit = _currentState.Guardrails.RetryLimit ?? 0;
            int llmAttempt = 0;
            while (true)
            {
                try
                {
                    llmResult = await CallLlmAsync(messages);
                    break;
                }
                catch (OperationCanceledException)
                {
                    LogStep(AgentReviLogger.Step.Error, "LLM call cancelled", parent: requestLog, level: LogLevel.Warning);
                    return Terminate(AgentExitReason.Cancelled);
                }
                catch (Exception ex)
                {
                    if (llmAttempt < retryLimit)
                    {
                        llmAttempt++;
                        Util.Log($"AgentRunner '{_profile.Name}': LLM call failed (retry {llmAttempt}/{retryLimit}): {ex.Message}");
                        LogStep(AgentReviLogger.Step.Error, $"LLM call failed (retry {llmAttempt}/{retryLimit}): {ex.Message}", parent: requestLog, level: LogLevel.Warning);
                        continue;
                    }

                    Util.Log($"AgentRunner '{_profile.Name}': LLM call failed: {ex.Message}");
                    LogStep(AgentReviLogger.Step.Error, $"LLM call failed: {ex.Message}", parent: requestLog, level: LogLevel.Error);
                    return Terminate(AgentExitReason.Error);
                }
            }

            _currentStateSteps++;
            _totalSteps++;

            // ── Accumulate actual cost from provider-reported usage ──────────
            if (llmResult != null)
            {
                ModelProfile? resolvedModel = ResolveModel();
                if (resolvedModel != null)
                {
                    decimal actualCost = ComputeCost(resolvedModel, llmResult.InputTokens, llmResult.OutputTokens);
                    _currentStateCost += actualCost;
                    _runTotalCost += actualCost;
                }
            }

            string rawResponse = llmResult?.Selected ?? "";
            _conversationHistory.Add(new Message("assistant", rawResponse));

            LogStep(
                AgentReviLogger.Step.LlmResponse,
                $"LLM response (step {_totalSteps})",
                parent: requestLog,
                object1: rawResponse,
                object1Name: "raw",
                object2: BuildCompletionMeta(llmResult),
                object2Name: "meta");

            // ── Deserialize structured response ──────────────────────────────
            AgentStepResponse? stepResponse = TryDeserializeStep(rawResponse);
            if (stepResponse == null)
            {
                Util.Log($"AgentRunner '{_profile.Name}': Could not deserialize step response in state '{_currentStateName}'. Raw: {rawResponse[..Math.Min(200, rawResponse.Length)]}");

                // Give the model a bounded chance to reformat (mirrors the unknown-signal nudge)
                // before giving up — models without enforced structured output occasionally stray.
                if (_malformedStepsThisActivation < MaxSignalCorrectionsPerActivation)
                {
                    _malformedStepsThisActivation++;
                    LogStep(AgentReviLogger.Step.Error, "Step response was not valid JSON — asking the model to reformat", parent: requestLog, level: LogLevel.Warning);
                    _conversationHistory.Add(new Message("user",
                        "Your previous reply could not be parsed. Respond with EXACTLY ONE JSON object matching the required format — "
                        + "no markdown code fences, no text before or after the JSON."));
                    continue;
                }

                LogStep(AgentReviLogger.Step.Error, "Failed to deserialize step response", parent: requestLog, level: LogLevel.Error);
                return Terminate(AgentExitReason.Error);
            }

            _lastContent = stepResponse.Content;

            // ── Surface optional thinking output ─────────────────────────────
            if (!string.IsNullOrWhiteSpace(stepResponse.Thinking))
            {
                LogStep(
                    AgentReviLogger.Step.Thinking,
                    "Model thinking",
                    parent: requestLog,
                    object1: stepResponse.Thinking,
                    object1Name: "thinking");
            }

            // ── Surface the assistant message as its own event (mirrors thinking) so
            //    trace consumers don't have to re-parse it out of the raw llm-response. ──
            if (!string.IsNullOrWhiteSpace(stepResponse.Content))
            {
                LogStep(
                    AgentReviLogger.Step.Content,
                    "Model message",
                    parent: requestLog,
                    object1: stepResponse.Content,
                    object1Name: "content");
            }

            // ── Execute tool calls IN PARALLEL ───────────────────────────────
            // A tool is allowed if the state lists it, or it's a file-access tool and the run has
            // attachments (so authors needn't list list-files/read-file just to use uploaded files).
            bool filesAttached = _ctx.Files is { Files.Count: > 0 };
            bool IsToolAllowed(string name) =>
                _currentState.Tools.Contains(name, StringComparer.OrdinalIgnoreCase)
                || (filesAttached && FileAccessTools.Names.Contains(name));

            var allowedCalls = stepResponse.ToolCalls
                .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
                .Where(tc => IsToolAllowed(tc.Name))
                .ToList();

            var disallowedCalls = stepResponse.ToolCalls
                .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
                .Where(tc => !IsToolAllowed(tc.Name))
                .ToList();

            foreach (var disallowed in disallowedCalls)
                Util.Log($"AgentRunner '{_profile.Name}': LLM requested disallowed tool '{disallowed.Name}' in state '{_currentStateName}' — ignored.");

            if (allowedCalls.Count > 0)
            {
                // Check tool call limit before executing
                int remaining = Math.Max(0, (_currentState.Guardrails.ToolCallLimit ?? int.MaxValue) - _currentStateToolCalls);
                var callsToRun = allowedCalls.Take(remaining).ToList();
                var droppedCalls = allowedCalls.Skip(callsToRun.Count).ToList();

                // Surface dropped-over-limit calls as an event (previously a silent Util.Log).
                if (droppedCalls.Count > 0)
                {
                    Util.Log($"AgentRunner '{_profile.Name}': Tool call limit reached in state '{_currentStateName}'. {droppedCalls.Count} call(s) dropped.");
                    LogStep(
                        AgentReviLogger.Step.ToolDropped,
                        $"{droppedCalls.Count} tool call(s) dropped — tool-call-limit ({_currentState.Guardrails.ToolCallLimit}) reached in state '{_currentStateName}'",
                        parent: requestLog,
                        object1: droppedCalls.Select(tc => new { name = tc.Name, input = tc.Input }).ToList(),
                        object1Name: "dropped",
                        level: LogLevel.Warning);
                }

                if (callsToRun.Count > 0)
                {
                    // Execute allowed tool calls concurrently, bounded by the state's
                    // max-parallel-tools guardrail (null = no cap). Excess calls queue on the
                    // semaphore and start as slots free. Each task pushes its own
                    // AgentRunContext so InvokeAgentTool sees the correct parent log.
                    int maxParallel = _currentState.Guardrails.MaxParallelTools ?? callsToRun.Count;
                    if (maxParallel < 1) maxParallel = 1;
                    var gate = new SemaphoreSlim(maxParallel, maxParallel);

                    ToolCallResult[] toolResults;
                    try
                    {
                        toolResults = await Task.WhenAll(
                            callsToRun.Select(tc => DispatchToolWithLogging(tc, requestLog, gate))
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        LogStep(AgentReviLogger.Step.Error, "Tool execution cancelled", level: LogLevel.Warning);
                        return Terminate(AgentExitReason.Cancelled);
                    }

                    _currentStateToolCalls += callsToRun.Count;

                    // Append all tool results to conversation history
                    foreach (var result in toolResults)
                        _conversationHistory.Add(new Message("user", result.ToHistoryMessage()));
                }
            }

            // ── Resolve transition ────────────────────────────────────────────
            string? signal = stepResponse.Signal?.Trim().ToUpperInvariant();
            LoopTransition? transition = ResolveTransition(_currentStateName, signal);

            if (transition == null)
            {
                // Two cases:
                //   (a) signal is non-null but unknown — typo / hallucination. Nudge the LLM.
                //   (b) signal is null and there's no unconditional fallback. Stay (existing behaviour).
                if (!string.IsNullOrEmpty(signal))
                {
                    _signalsCorrectedThisActivation++;

                    var validSignals = _profile.ValidSignalsByState.TryGetValue(_currentStateName, out var s)
                        ? s
                        : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (_signalsCorrectedThisActivation > MaxSignalCorrectionsPerActivation)
                    {
                        string termMsg = $"State '{_currentStateName}' received {_signalsCorrectedThisActivation} unknown signals " +
                                         $"(latest: '{signal}'); declared signals are " +
                                         $"[{string.Join(", ", validSignals)}].";
                        Util.Log($"AgentRunner '{_profile.Name}': {termMsg}");
                        LogStep(AgentReviLogger.Step.Error, termMsg, level: LogLevel.Error);
                        return Terminate(AgentExitReason.InvalidSignal, guardrailMessage: termMsg);
                    }

                    string validList = validSignals.Count > 0
                        ? string.Join(", ", validSignals)
                        : "(none declared from this state)";
                    string nudge = $"Signal '{signal}' is not valid from state '{_currentStateName}'. " +
                                   $"Valid signals are: {validList}. " +
                                   "Re-emit your decision with one of these signals.";
                    _conversationHistory.Add(new Message("user", nudge));

                    Util.Log($"AgentRunner '{_profile.Name}': Nudging LLM after unknown signal '{signal}' (valid: {validList}).");
                    LogStep(
                        AgentReviLogger.Step.Error,
                        $"Unknown signal '{signal}' from state '{_currentStateName}'; nudged LLM with valid set.",
                        object1: new { signal, validSignals = validSignals.ToArray() },
                        object1Name: "signal-correction",
                        level: LogLevel.Warning);
                }
                else
                {
                    Util.Log($"AgentRunner '{_profile.Name}': No transition found (no signal, no fallback) in state '{_currentStateName}'. Staying.");
                }
                continue;
            }

            string target = transition.TargetState;

            if (string.Equals(target, "[end]", StringComparison.OrdinalIgnoreCase))
            {
                Util.Log($"AgentRunner '{_profile.Name}': Reached [end]. Completing.");
                return Complete();
            }

            string nextState = string.Equals(target, "self", StringComparison.OrdinalIgnoreCase)
                ? _currentStateName
                : target;

            if (string.Equals(nextState, _currentStateName, StringComparison.OrdinalIgnoreCase))
            {
                // Self-loop: stay in the same activation context (keep accumulating step/tool/cost counters),
                // but count it as a re-activation so cycle-limit bounds self-looping states as documented.
                _currentStateCycles++;
                continue;
            }

            // Validate the target state exists
            if (!_profile.States.Any(s => string.Equals(s.Name, nextState, StringComparison.OrdinalIgnoreCase)))
            {
                Util.Log($"AgentRunner '{_profile.Name}': Transition target '{nextState}' is not a defined state.");
                LogStep(AgentReviLogger.Step.Error, $"Transition target '{nextState}' is not a defined state", level: LogLevel.Error);
                return Terminate(AgentExitReason.Error);
            }

            LogStep(
                AgentReviLogger.Step.StateTransition,
                $"Transition '{_currentStateName}' -> '{nextState}' on signal '{signal ?? "(unconditional)"}'",
                object1: new { from = _currentStateName, to = nextState, signal },
                object1Name: "transition",
                object2: _stateTraversalHistory.ToArray(),
                object2Name: "history");

            TransitionToState(nextState);
        }
    }


    // ==============
    //  State Transitions
    // ==============

    private void TransitionToState(string stateName, bool isEntry = false)
    {
        _currentStateName = stateName;
        _currentState = _profile.States.First(s =>
            string.Equals(s.Name, stateName, StringComparison.OrdinalIgnoreCase));

        // Increment cycle count (how many times this state has been activated)
        _currentStateCycles++;

        // Reset per-activation counters
        _currentStateSteps = 0;
        _currentStateToolCalls = 0;
        _signalsCorrectedThisActivation = 0;
        _malformedStepsThisActivation = 0;
        _currentStateCost = 0m;
        _currentStateBudgetWarned = false;
        _stateActivatedAt = DateTime.UtcNow;

        _stateTraversalHistory.Add(stateName);
        Util.Log($"AgentRunner '{_profile.Name}': Entered state '{stateName}' (cycle {_currentStateCycles}).");
    }


    // ==============
    //  Guardrails
    // ==============

    private (bool violated, string? message) CheckGuardrails()
    {
        var g = _currentState.Guardrails;

        if (g.CycleLimit.HasValue && _currentStateCycles > g.CycleLimit.Value)
            return (true, $"State '{_currentStateName}' cycle limit ({g.CycleLimit.Value}) exceeded.");

        if (g.MaxSteps.HasValue && _currentStateSteps >= g.MaxSteps.Value)
            return (true, $"State '{_currentStateName}' max-steps ({g.MaxSteps.Value}) exceeded.");

        if (g.TimeoutSeconds.HasValue)
        {
            double elapsed = (DateTime.UtcNow - _stateActivatedAt).TotalSeconds;
            if (elapsed > g.TimeoutSeconds.Value)
                return (true, $"State '{_currentStateName}' timeout ({g.TimeoutSeconds.Value}s) exceeded.");
        }

        return (false, null);
    }

    /// <summary>
    /// Checks the projected cost of the next LLM call against the state-level and run-wide
    /// cost budgets. Returns a violation message that callers should pair with
    /// <see cref="AgentExitReason.BudgetExceeded"/>. Emits a one-shot 80%-of-budget warning
    /// event the first time either budget crosses that threshold.
    /// </summary>
    private (bool exceeded, string? message) CheckBudget()
    {
        decimal? stateBudget = _currentState.Guardrails.CostBudget;
        decimal? runBudget = _profile.RunCostBudget;

        if (!stateBudget.HasValue && !runBudget.HasValue)
            return (false, null);

        decimal projected = ProjectNextCallCost();

        if (stateBudget.HasValue)
        {
            decimal projectedTotal = _currentStateCost + projected;
            if (projectedTotal > stateBudget.Value)
            {
                return (true, $"State '{_currentStateName}' cost-budget would be exceeded " +
                              $"(budget {stateBudget.Value:0.####}, spent {_currentStateCost:0.####}, " +
                              $"projected next call ~{projected:0.####}).");
            }

            if (!_currentStateBudgetWarned && projectedTotal >= stateBudget.Value * 0.80m)
            {
                _currentStateBudgetWarned = true;
                LogStep(
                    AgentReviLogger.Step.GuardrailViolation,
                    $"State cost-budget at {(projectedTotal / stateBudget.Value):P0} " +
                    $"(spent {_currentStateCost:0.####} of {stateBudget.Value:0.####}).",
                    level: LogLevel.Warning);
            }
        }

        if (runBudget.HasValue)
        {
            decimal projectedTotal = _runTotalCost + projected;
            if (projectedTotal > runBudget.Value)
            {
                return (true, $"Run cost-budget would be exceeded " +
                              $"(budget {runBudget.Value:0.####}, spent {_runTotalCost:0.####}, " +
                              $"projected next call ~{projected:0.####}).");
            }

            if (!_runBudgetWarned && projectedTotal >= runBudget.Value * 0.80m)
            {
                _runBudgetWarned = true;
                LogStep(
                    AgentReviLogger.Step.GuardrailViolation,
                    $"Run cost-budget at {(projectedTotal / runBudget.Value):P0} " +
                    $"(spent {_runTotalCost:0.####} of {runBudget.Value:0.####}).",
                    level: LogLevel.Warning);
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Worst-case USD cost for the next LLM call against the current state's resolved model.
    /// Estimates input tokens from the current message buffer (rough char/4 heuristic) and
    /// output tokens from the model's configured MaxTokens (or DefaultProjectedOutputTokens).
    /// Returns 0 when the model has no cost rates configured.
    /// </summary>
    private decimal ProjectNextCallCost()
    {
        ModelProfile? model = ResolveModel();
        if (model == null) return 0m;
        if (!model.CostPerMillionInputTokens.HasValue && !model.CostPerMillionOutputTokens.HasValue)
            return 0m;

        int approxInputChars = 0;
        if (!string.IsNullOrEmpty(_profile.SystemPrompt))
            approxInputChars += _profile.SystemPrompt.Length;
        if (!string.IsNullOrEmpty(_currentState.Instruction))
            approxInputChars += _currentState.Instruction.Length;
        foreach (var m in _conversationHistory)
            if (!string.IsNullOrEmpty(m.Content))
                approxInputChars += m.Content.Length;

        // ~4 characters per token is the standard rough estimate for English text.
        int estInputTokens = Math.Max(1, approxInputChars / 4);

        int estOutputTokens = ParseInt(model.OutputBudget) ?? DefaultProjectedOutputTokens;

        return ComputeCost(model, estInputTokens, estOutputTokens);
    }

    /// <summary>
    /// Translates a token usage pair into USD cost using the model's per-million-token rates.
    /// Either rate may be unset (no cost contribution from that side).
    /// </summary>
    private static decimal ComputeCost(ModelProfile model, int? inputTokens, int? outputTokens)
    {
        decimal cost = 0m;
        if (inputTokens.HasValue && model.CostPerMillionInputTokens.HasValue)
            cost += (decimal)inputTokens.Value / 1_000_000m * model.CostPerMillionInputTokens.Value;
        if (outputTokens.HasValue && model.CostPerMillionOutputTokens.HasValue)
            cost += (decimal)outputTokens.Value / 1_000_000m * model.CostPerMillionOutputTokens.Value;
        return cost;
    }

    /// <summary>
    /// Resolves the model profile for the current state. Mirrors the lookup logic in
    /// <see cref="CallLlmAsync"/> but does not throw — returns null when no eligible
    /// model is configured (cost projection is skipped in that case).
    /// </summary>
    private ModelProfile? ResolveModel()
    {
        // A per-run override (isolation path) wins over per-state registry resolution.
        if (_modelOverride != null)
            return _modelOverride;

        ModelProfile? model = null;
        if (!string.IsNullOrWhiteSpace(_currentState.Model))
            model = _models.Get(_currentState.Model);
        model ??= _models.Find(ModelTier.A);
        return model;
    }

    /// <summary>
    /// Sliding-window loop detection. Checks whether the most recent state traversal history
    /// contains a repeated sub-sequence of any length (1 to n/2).
    /// </summary>
    private bool DetectLoop()
    {
        int n = _stateTraversalHistory.Count;
        if (n < 4) return false; // Need at least 2 repetitions of length-2

        for (int len = 1; len <= n / 2; len++)
        {
            bool repeats = true;
            for (int i = 0; i < len; i++)
            {
                if (_stateTraversalHistory[n - 1 - i] != _stateTraversalHistory[n - 1 - i - len])
                {
                    repeats = false;
                    break;
                }
            }
            if (repeats) return true;
        }
        return false;
    }


    // ==============
    //  Prompt Building
    // ==============

    private string BuildInitialUserMessage()
    {
        if (_inputs.Count == 0)
            return "Begin.";

        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in _inputs)
            sb.AppendLine($"[{key}]: {value}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the full message list for the current step.
    /// System message is composed (in order) from:
    ///   1. agent-level <c>[[_system]]</c> prompt,
    ///   2. the <c>state.X.prompt</c>-referenced .pmt file's system + instruction (if set, with
    ///      <c>{key}</c> placeholders substituted from the agent's initial inputs),
    ///   3. the inline <c>[[_state.X.instruction]]</c> text (if set; appended after the resolved
    ///      prompt's instruction so it can act as a per-run override).
    /// Sections are joined by <c>\n\n---\n\n</c>. Followed by the full conversation history.
    /// </summary>
    private List<Message> BuildStepMessages()
    {
        var messages = new List<Message>();

        var systemParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_profile.SystemPrompt))
            systemParts.Add(_profile.SystemPrompt);

        // Resolve the state's named prompt reference, if any.
        if (!string.IsNullOrWhiteSpace(_currentState.Prompt))
        {
            Prompt? resolved = _prompts.Get(_currentState.Prompt);
            if (resolved != null)
            {
                if (!string.IsNullOrWhiteSpace(resolved.System))
                    systemParts.Add(SubstituteInputs(resolved.System));
                if (!string.IsNullOrWhiteSpace(resolved.Instruction))
                    systemParts.Add(SubstituteInputs(resolved.Instruction));
            }
            else
            {
                Util.Log($"AgentRunner '{_profile.Name}': Prompt '{_currentState.Prompt}' referenced from state " +
                         $"'{_currentStateName}' was not found. Falling back to inline instruction only.");
            }
        }

        if (!string.IsNullOrWhiteSpace(_currentState.Instruction))
            systemParts.Add(SubstituteInputs(_currentState.Instruction));

        // When the run has attached files, tell the agent they exist and how to read them. The raw
        // contents are never dumped here — the agent uses list-files / read-file (a reader model
        // reads each file against a focused query) to pull only what it needs.
        if (_ctx.Files is { Files.Count: > 0 } registry)
        {
            var fb = new System.Text.StringBuilder();
            fb.AppendLine("ATTACHED FILES — the user attached these files to this session:");
            foreach (var f in registry.Files)
                fb.AppendLine($"- {f.Name} ({f.MediaType}, {f.Size:N0} bytes){(f.IsImage ? " [image]" : "")}");
            fb.Append("Do not assume their contents. Use the `list-files` tool to enumerate them and the "
                + "`read-file` tool ({\"file\":\"<name>\",\"query\":\"<what you need>\"}) to have a reader model "
                + "read a file (text or image) and answer a focused question. Use `search-files` to query across all of them.");
            systemParts.Add(fb.ToString());
        }

        // Spell out the required JSON step contract on every call. Providers with structured-output
        // guidance (e.g. Gemini's responseSchema) are constrained to it directly; providers without
        // it (e.g. Claude, whose provider sets supports-guidance=false) rely entirely on this
        // instruction to return a parseable AgentStepResponse instead of prose.
        systemParts.Add(BuildResponseFormatInstruction());

        string systemText = string.Join("\n\n---\n\n", systemParts);
        if (!string.IsNullOrWhiteSpace(systemText))
            messages.Add(new Message("system", systemText));

        messages.AddRange(_conversationHistory);
        return messages;
    }

    /// <summary>
    /// Describes the mandatory <see cref="AgentStepResponse"/> JSON contract for the current step,
    /// including the transition signals valid from this state and the tools available. Injected into
    /// the system prompt so every provider — guidance-capable or not — knows exactly what to emit.
    /// </summary>
    private string BuildResponseFormatInstruction()
    {
        string signals = _profile.ValidSignalsByState.TryGetValue(_currentStateName, out var sigs) && sigs.Count > 0
            ? string.Join(", ", sigs)
            : "(none declared for this state)";

        string toolGuide = BuildToolGuide();

        return
            "RESPONSE FORMAT — reply with EXACTLY ONE JSON object and nothing else: no markdown code "
            + "fences, no text before or after it. The object must have these keys:\n"
            + "{\"signal\": <string|null>, \"tool_calls\": [{\"name\": <string>, \"input\": <string>}], "
            + "\"content\": <string>, \"thinking\": <string|null>}\n"
            + $"- \"signal\": the state transition to take now. Valid signals from this state: {signals}. "
            + "Use null to take no transition this step.\n"
            + "- \"tool_calls\": tools to run this step; each \"input\" is a single string in the format the "
            + "tool expects (see below). Use [] when calling none. Available tools:\n"
            + toolGuide + "\n"
            + "- \"content\": your message or result for this step.\n"
            + "- \"thinking\": optional brief reasoning, or null.";
    }

    /// <summary>
    /// Renders the tools available from the current state — name + description + expected input format —
    /// so the model has the exact tool names and input shapes without the author hand-copying them into the
    /// prompt. Descriptions come from <see cref="IBuiltInTool.Description"/> (built-in), the custom
    /// <see cref="ToolProfile.Description"/> (.tool files), or a built-in fallback for the file tools.
    /// </summary>
    private string BuildToolGuide()
    {
        var names = new List<string>(_currentState.Tools);
        if (_ctx.Files is { Files.Count: > 0 })
            names.AddRange(new[] { "list-files", "read-file", "search-files" });

        if (names.Count == 0)
            return "  (none available — use an empty tool_calls array)";

        var sb = new System.Text.StringBuilder();
        foreach (var name in names)
        {
            string? desc = _tools.GetBuiltIn(name)?.Description
                ?? _tools.GetCustom(name)?.Description
                ?? FileToolDescription(name);
            sb.AppendLine(string.IsNullOrWhiteSpace(desc) ? $"  - {name}" : $"  - {name}: {desc}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Static descriptions (with input format) for the session file tools when not registered as built-ins.</summary>
    private static string? FileToolDescription(string name) => name switch
    {
        "list-files" => "Lists the attached files (name, media type, size). Input: an empty string.",
        "read-file" => "Has a reader model read one attached file (text or image) and answer a focused question. "
                       + "Input JSON: {\"file\":\"<name>\",\"query\":\"<what you need>\"}.",
        "search-files" => "Searches across all attached files for relevant content. Input: a search query string.",
        _ => null,
    };

    /// <summary>
    /// Replaces <c>{identifier}</c> placeholders in the given text with the corresponding
    /// agent input value. Identifiers are derived from input keys via Util.Identifierize so
    /// callers may use natural keys ("topic", "Issue Title") that resolve to the canonical
    /// placeholder form.
    /// </summary>
    private string SubstituteInputs(string text)
    {
        if (string.IsNullOrEmpty(text) || _inputs.Count == 0)
            return text;

        foreach (var (key, value) in _inputs)
        {
            string placeholder = "{" + Util.Identifierize(key) + "}";
            if (text.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
                text = Regex.Replace(text, Regex.Escape(placeholder), value?.ToString() ?? "", RegexOptions.IgnoreCase);
        }

        return text;
    }


    // ==============
    //  LLM Invocation
    // ==============

    private async Task<CompletionResult?> CallLlmAsync(List<Message> messages)
    {
        // Resolve model: per-run override → state override → _models.Find(Tier.A)
        ModelProfile? model = _modelOverride;
        if (model == null)
        {
            if (!string.IsNullOrWhiteSpace(_currentState.Model))
                model = _models.Get(_currentState.Model);
            model ??= _models.Find(ModelTier.A);
        }

        if (model == null)
            throw new Exception($"AgentRunner '{_profile.Name}': No eligible model found.");

        if (model.Provider?.InferenceClient is null)
            throw new Exception($"AgentRunner '{_profile.Name}': Model '{model.Name}' has no inference client.");

        // Apply state-level inline settings overrides on top of model defaults.
        var s = _currentState.InlineSettings;
        int estInputTokens = Util.EstTokenCountFromCharCount(messages.Sum(m => m.Role.Length + m.Content.Length));
        CompletionResult? result = await model.Provider.InferenceClient.GenerateAsync(
            messages: messages,
            model: model.ModelString,
            temperature: s?.Temperature ?? ParseFloat(model.Temperature),
            topK: s?.TopK ?? ParseInt(model.TopK),
            topP: s?.TopP ?? ParseFloat(model.TopP),
            minP: s?.MinP ?? ParseFloat(model.MinP),
            bestOf: s?.BestOf ?? null,
            maxTokenType: model.MaxTokenType,
            maxTokens: TokenBudgetGuard.Clamp(s?.OutputBudget ?? ParseInt(model.OutputBudget), estInputTokens, model, _profile.Name ?? "(agent)"),
            frequencyPenalty: s?.FrequencyPenalty ?? ParseFloat(model.FrequencyPenalty),
            presencePenalty: s?.PresencePenalty ?? ParseFloat(model.PresencePenalty),
            repetitionPenalty: s?.RepetitionPenalty ?? ParseFloat(model.RepetitionPenalty),
            stopSequences: null,
            guidanceType: GuidanceType.Json,
            guidanceString: AgentStepSchema.Schema,
            useSearchGrounding: s?.UseSearchGrounding ?? null,
            cancellationToken: _token,
            inactivityTimeoutSeconds: _currentState.Guardrails.TimeoutSeconds);

        // Loop-detection circuit breaker (post-hoc): flag a degenerate repetition loop with a truthful
        // finish reason so the step handling and the trace (BuildCompletionMeta stamps finishReason into
        // every llm-response event) can see it.
        if (result is not null &&
            RepetitionDetector.TryDetect(result.Selected, model.LoopDetection, out string loopEvidence))
        {
            Util.Log($"RepetitionDetector: agent '{_profile.Name}' step on model '{model.Name}' produced a degenerate loop — {loopEvidence}");
            result.FinishReason = "repetition";
        }

        return result;
    }


    // ==============
    //  Step Deserialization
    // ==============

    private static AgentStepResponse? TryDeserializeStep(string raw)
    {
        var parsed = StepJsonParser.Parse(raw);
        if (parsed == null && !string.IsNullOrWhiteSpace(raw))
            Util.Log($"AgentRunner: Failed to deserialize step response from: {(raw.Length > 300 ? raw[..300] + "…" : raw)}");
        return parsed;
    }


    // ==============
    //  Tool Execution
    // ==============

    /// <summary>
    /// Wraps a single tool dispatch with tool-call / tool-start / tool-result events and pushes
    /// an AgentRunContext so InvokeAgentTool sees the correct parent log.
    ///   • The tool-call event nests under <paramref name="stepLog"/> (the step's llm-request) and
    ///     is emitted immediately, so the call is observable as "queued" while it waits for a slot.
    ///   • <paramref name="gate"/> bounds how many calls from this step run concurrently; a
    ///     tool-start event marks the queued → running transition once a slot is acquired.
    /// </summary>
    private async Task<ToolCallResult> DispatchToolWithLogging(AgentToolCall tc, Rlog stepLog, SemaphoreSlim gate)
    {
        Rlog toolCallRlog = LogStep(
            AgentReviLogger.Step.ToolCall,
            $"Tool call: {tc.Name}",
            parent: stepLog,
            object1: tc.Input,
            object1Name: "input",
            object2: tc.Name,
            object2Name: "tool");

        await gate.WaitAsync(_token);
        try
        {
            // Slot acquired: the call moves from queued to running.
            LogStep(
                AgentReviLogger.Step.ToolStart,
                $"Tool started: {tc.Name}",
                parent: toolCallRlog);

            ToolCallResult result;
            // Carry the current state's max-agent-depth guardrail so InvokeAgentTool enforces the
            // per-state override rather than only the runner-wide default.
            AgentRunContext childCtx = _ctx.Child(toolCallRlog, _currentState.Guardrails.MaxAgentDepth);
            using (AgentRunContext.Push(childCtx))
            {
                result = await ExecuteToolAsync(tc.Name, tc.Input);
            }

            AgentReviLogger.LogStep(
                parent: toolCallRlog,
                agentName: _profile.Name ?? "(unnamed)",
                sessionId: SessionId,
                stepType: AgentReviLogger.Step.ToolResult,
                stateName: _currentStateName,
                cycle: _currentStateCycles,
                depth: _ctx.Depth,
                message: result.Failed
                    ? $"Tool result (failed): {tc.Name}"
                    : $"Tool result: {tc.Name}",
                object1: result.Output,
                object1Name: "output",
                object2: new { failed = result.Failed, error = result.ErrorMessage },
                object2Name: "status",
                level: result.Failed ? LogLevel.Warning : LogLevel.Info);

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ToolCallResult> ExecuteToolAsync(string toolName, string input)
    {
        // Check built-in registry first
        IBuiltInTool? builtIn = _tools.GetBuiltIn(toolName);
        if (builtIn != null)
        {
            try { return await builtIn.ExecuteAsync(input, _token); }
            catch (Exception ex)
            {
                return new ToolCallResult { ToolName = toolName, Failed = true, ErrorMessage = ex.Message };
            }
        }

        // Check custom tool profiles (MCP/HTTP)
        ToolProfile? customTool = _tools.GetCustom(toolName);
        if (customTool != null)
            return await ExecuteCustomToolAsync(customTool, input);

        return new ToolCallResult
        {
            ToolName = toolName,
            Failed = true,
            ErrorMessage = $"Tool '{toolName}' is not registered as a built-in or custom tool."
        };
    }

    private Task<ToolCallResult> ExecuteCustomToolAsync(ToolProfile profile, string input)
    {
        // MCP stdio and HTTP stubs — full implementation deferred
        return Task.FromResult(new ToolCallResult
        {
            ToolName = profile.Name ?? "",
            Failed = true,
            ErrorMessage = $"Custom tool type '{profile.Type}' execution is not yet implemented."
        });
    }


    // ==============
    //  Transition Resolution
    // ==============

    private LoopTransition? ResolveTransition(string stateName, string? signal)
    {
        var node = _profile.LoopGraph.FirstOrDefault(n =>
            string.Equals(n.StateName, stateName, StringComparison.OrdinalIgnoreCase));

        if (node == null) return null;

        // Try signal-matched transition first
        if (!string.IsNullOrEmpty(signal))
        {
            var matched = node.Transitions.FirstOrDefault(t =>
                string.Equals(t.Signal, signal, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }

        // Fallback: first unconditional transition (no [when:] clause)
        return node.Transitions.FirstOrDefault(t => t.Signal == null);
    }


    // ==============
    //  Termination
    // ==============

    private AgentResult Complete()
    {
        var result = new AgentResult
        {
            FinalOutput = _lastContent,
            ExitReason = AgentExitReason.Completed,
            StateHistory = new List<string>(_stateTraversalHistory),
            TotalSteps = _totalSteps,
            SessionId = SessionId,
            Cost = _runTotalCost
        };
        LogEnd(result);
        return result;
    }

    private AgentResult Terminate(AgentExitReason reason, string? guardrailMessage = null)
    {
        var result = new AgentResult
        {
            FinalOutput = _lastContent,
            ExitReason = reason,
            StateHistory = new List<string>(_stateTraversalHistory),
            TotalSteps = _totalSteps,
            GuardrailViolationMessage = guardrailMessage,
            SessionId = SessionId,
            Cost = _runTotalCost
        };
        LogEnd(result);
        return result;
    }

    private void LogEnd(AgentResult result)
    {
        LogStep(
            AgentReviLogger.Step.End,
            $"Agent run ended: {result.ExitReason}",
            object1: result.FinalOutput,
            object1Name: "final-output",
            object2: new
            {
                exitReason = result.ExitReason.ToString(),
                totalSteps = result.TotalSteps,
                stateHistory = result.StateHistory,
                guardrailMessage = result.GuardrailViolationMessage
            },
            object2Name: "result");
    }


    // ==============
    //  Logging Helper
    // ==============

    private Rlog LogStep(
        string stepType,
        string message,
        Rlog? parent = null,
        object? object1 = null,
        string? object1Name = null,
        object? object2 = null,
        string? object2Name = null,
        LogLevel level = LogLevel.Info)
    {
        return AgentReviLogger.LogStep(
            parent: parent ?? _runRoot,
            agentName: _profile.Name ?? "(unnamed)",
            sessionId: SessionId,
            stepType: stepType,
            stateName: _currentStateName,
            cycle: _currentStateCycles,
            depth: _ctx.Depth,
            message: message,
            object1: object1,
            object1Name: object1Name,
            object2: object2,
            object2Name: object2Name,
            level: level);
    }


    // ==============
    //  Profile / Completion summaries (for trace payloads)
    // ==============

    private object BuildProfileSummary() => new
    {
        name = _profile.Name,
        version = _profile.Version,
        description = _profile.Description,
        entryState = _profile.EntryState,
        states = _profile.States.Select(s => new
        {
            name = s.Name,
            description = s.Description,
            tools = s.Tools,
            model = s.Model
        }).ToList()
    };

    private static object BuildCompletionMeta(CompletionResult? result)
    {
        if (result == null) return new { };
        return new
        {
            inputTokens = TryGetIntProperty(result, "InputTokens"),
            outputTokens = TryGetIntProperty(result, "OutputTokens"),
            model = TryGetStringProperty(result, "Model"),
            // Surfaced so trace consumers (e.g. Refinery truncation invariants) can detect steps that
            // hit the output-token ceiling ("max_tokens" / "length") without re-querying the provider.
            finishReason = TryGetStringProperty(result, "FinishReason")
        };
    }

    private static int? TryGetIntProperty(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name);
        if (prop == null) return null;
        try { return (int?)Convert.ChangeType(prop.GetValue(obj), typeof(int)); } catch { return null; }
    }

    private static string? TryGetStringProperty(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name);
        return prop?.GetValue(obj) as string;
    }


    // ==============
    //  Type Parsers (model tuning properties are stored as strings)
    // ==============

    private static float? ParseFloat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "disabled") return null;
        return float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "disabled") return null;
        return int.TryParse(value, out int i) ? i : null;
    }
}
