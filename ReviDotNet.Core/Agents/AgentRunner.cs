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

    /// <summary>Unique id for this agent activation. Tagged on every emitted log event.</summary>
    public string SessionId { get; }

    /// <summary>The run-root Rlog. All step events for this run are children of it.</summary>
    private readonly Rlog _runRoot;

    private string _currentStateName = "";
    private AgentState _currentState = null!;

    // Per-activation counters (reset on state transition)
    private int _currentStateCycles;   // number of times the current state has been activated
    private int _currentStateSteps;    // LLM calls in the current activation
    private int _currentStateToolCalls; // tool calls dispatched in the current activation
    private int _signalsCorrectedThisActivation; // unknown-signal nudges sent in the current activation
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

    public AgentRunner(AgentProfile profile, Dictionary<string, object> inputs, CancellationToken token)
        : this(profile, inputs, token, AgentRunContext.Root())
    {
    }

    public AgentRunner(AgentProfile profile, Dictionary<string, object> inputs, CancellationToken token, AgentRunContext ctx)
    {
        _profile = profile;
        _inputs = inputs;
        _token = token;
        _ctx = ctx;

        SessionId = Guid.NewGuid().ToString("n");

        // Emit the run-root event before any work begins. All step events nest under this.
        _runRoot = AgentReviLogger.LogStart(
            parentLog: _ctx.ParentLog,
            agentName: _profile.Name ?? "(unnamed)",
            sessionId: SessionId,
            depth: _ctx.Depth,
            entryState: _profile.EntryState ?? "",
            inputs: _inputs,
            profileSummary: BuildProfileSummary());
    }


    // ==============
    //  Main Loop
    // ==============

    public async Task<AgentResult> RunAsync()
    {
        // Transition into the entry state (no state-transition event for the seed entry)
        TransitionToState(_profile.EntryState!, isEntry: true);

        // Seed the conversation with the initial user inputs
        string initialMessage = BuildInitialUserMessage();
        _conversationHistory.Add(new Message("user", initialMessage));

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

            try
            {
                llmResult = await CallLlmAsync(messages);
            }
            catch (OperationCanceledException)
            {
                LogStep(AgentReviLogger.Step.Error, "LLM call cancelled", parent: requestLog, level: LogLevel.Warning);
                return Terminate(AgentExitReason.Cancelled);
            }
            catch (Exception ex)
            {
                Util.Log($"AgentRunner '{_profile.Name}': LLM call failed: {ex.Message}");
                LogStep(AgentReviLogger.Step.Error, $"LLM call failed: {ex.Message}", parent: requestLog, level: LogLevel.Error);
                return Terminate(AgentExitReason.Error);
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

            // ── Execute tool calls IN PARALLEL ───────────────────────────────
            var allowedCalls = stepResponse.ToolCalls
                .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
                .Where(tc => _currentState.Tools.Contains(tc.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var disallowedCalls = stepResponse.ToolCalls
                .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
                .Where(tc => !_currentState.Tools.Contains(tc.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var disallowed in disallowedCalls)
                Util.Log($"AgentRunner '{_profile.Name}': LLM requested disallowed tool '{disallowed.Name}' in state '{_currentStateName}' — ignored.");

            if (allowedCalls.Count > 0)
            {
                // Check tool call limit before executing
                int remaining = (_currentState.Guardrails.ToolCallLimit ?? int.MaxValue) - _currentStateToolCalls;
                var callsToRun = allowedCalls.Take(remaining).ToList();

                if (callsToRun.Count < allowedCalls.Count)
                    Util.Log($"AgentRunner '{_profile.Name}': Tool call limit reached in state '{_currentStateName}'. {allowedCalls.Count - callsToRun.Count} call(s) dropped.");

                if (callsToRun.Count > 0)
                {
                    // Execute all allowed tool calls concurrently. Each task pushes its own
                    // AgentRunContext so InvokeAgentTool sees the correct parent log.
                    ToolCallResult[] toolResults = await Task.WhenAll(
                        callsToRun.Select(tc => DispatchToolWithLogging(tc))
                    );

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
                // Self-loop: don't reset step/tool counters, don't increment cycle
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

        int estOutputTokens = ParseInt(model.MaxTokens) ?? DefaultProjectedOutputTokens;

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
        ModelProfile? model = null;
        if (!string.IsNullOrWhiteSpace(_currentState.Model))
            model = ModelManager.Get(_currentState.Model);
        model ??= ModelManager.Find(ModelTier.A);
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
            Prompt? resolved = PromptManager.Get(_currentState.Prompt);
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

        string systemText = string.Join("\n\n---\n\n", systemParts);
        if (!string.IsNullOrWhiteSpace(systemText))
            messages.Add(new Message("system", systemText));

        messages.AddRange(_conversationHistory);
        return messages;
    }

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
        // Resolve model: state override → ModelManager.Find(Tier.A)
        ModelProfile? model = null;
        if (!string.IsNullOrWhiteSpace(_currentState.Model))
            model = ModelManager.Get(_currentState.Model);
        model ??= ModelManager.Find(ModelTier.A);

        if (model == null)
            throw new Exception($"AgentRunner '{_profile.Name}': No eligible model found.");

        if (model.Provider?.InferenceClient is null)
            throw new Exception($"AgentRunner '{_profile.Name}': Model '{model.Name}' has no inference client.");

        // Apply state-level inline settings overrides on top of model defaults.
        var s = _currentState.InlineSettings;
        return await model.Provider.InferenceClient.GenerateAsync(
            messages: messages,
            model: model.ModelString,
            temperature: s?.Temperature ?? ParseFloat(model.Temperature),
            topK: s?.TopK ?? ParseInt(model.TopK),
            topP: s?.TopP ?? ParseFloat(model.TopP),
            minP: s?.MinP ?? ParseFloat(model.MinP),
            bestOf: s?.BestOf ?? null,
            maxTokenType: model.MaxTokenType,
            maxTokens: s?.MaxTokens ?? ParseInt(model.MaxTokens),
            frequencyPenalty: s?.FrequencyPenalty ?? ParseFloat(model.FrequencyPenalty),
            presencePenalty: s?.PresencePenalty ?? ParseFloat(model.PresencePenalty),
            repetitionPenalty: s?.RepetitionPenalty ?? ParseFloat(model.RepetitionPenalty),
            stopSequences: null,
            guidanceType: GuidanceType.Json,
            guidanceString: AgentStepSchema.Schema,
            useSearchGrounding: s?.UseSearchGrounding ?? null,
            cancellationToken: _token,
            inactivityTimeoutSeconds: _currentState.Guardrails.TimeoutSeconds);
    }


    // ==============
    //  Step Deserialization
    // ==============

    private static AgentStepResponse? TryDeserializeStep(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<AgentStepResponse>(raw);
        }
        catch (Exception ex)
        {
            Util.Log($"AgentRunner: Failed to deserialize step response: {ex.Message}");
            return null;
        }
    }


    // ==============
    //  Tool Execution
    // ==============

    /// <summary>
    /// Wraps a single tool dispatch with tool-call / tool-result events and pushes
    /// an AgentRunContext so InvokeAgentTool sees the correct parent log.
    /// </summary>
    private async Task<ToolCallResult> DispatchToolWithLogging(AgentToolCall tc)
    {
        Rlog toolCallRlog = LogStep(
            AgentReviLogger.Step.ToolCall,
            $"Tool call: {tc.Name}",
            object1: tc.Input,
            object1Name: "input",
            object2: tc.Name,
            object2Name: "tool");

        ToolCallResult result;
        AgentRunContext childCtx = _ctx.Child(toolCallRlog);
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

    private async Task<ToolCallResult> ExecuteToolAsync(string toolName, string input)
    {
        // Check built-in registry first
        var builtIn = ToolManager.GetBuiltIn(toolName);
        if (builtIn != null)
        {
            try { return await builtIn.ExecuteAsync(input, _token); }
            catch (Exception ex)
            {
                return new ToolCallResult { ToolName = toolName, Failed = true, ErrorMessage = ex.Message };
            }
        }

        // Check custom tool profiles (MCP/HTTP)
        var customTool = ToolManager.GetCustom(toolName);
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
            TotalSteps = _totalSteps
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
            GuardrailViolationMessage = guardrailMessage
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
            model = TryGetStringProperty(result, "Model")
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
