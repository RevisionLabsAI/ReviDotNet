// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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
    private DateTime _stateActivatedAt;

    // Run-wide tracking
    private readonly List<Message> _conversationHistory = new();
    private readonly List<string> _stateTraversalHistory = new();
    private int _totalSteps;
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
                // No matching transition — stay in current state for next step
                Util.Log($"AgentRunner '{_profile.Name}': No transition found for signal '{signal}' in state '{_currentStateName}'. Staying.");
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
    /// System message = agent global system prompt + current state instruction.
    /// Followed by the full conversation history.
    /// </summary>
    private List<Message> BuildStepMessages()
    {
        var messages = new List<Message>();

        // Combine agent system prompt with state-specific instruction
        var systemParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_profile.SystemPrompt))
            systemParts.Add(_profile.SystemPrompt);
        if (!string.IsNullOrWhiteSpace(_currentState.Instruction))
            systemParts.Add(_currentState.Instruction);

        string systemText = string.Join("\n\n---\n\n", systemParts);
        if (!string.IsNullOrWhiteSpace(systemText))
            messages.Add(new Message("system", systemText));

        messages.AddRange(_conversationHistory);
        return messages;
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

        return await model.Provider.InferenceClient.GenerateAsync(
            messages: messages,
            model: model.ModelString,
            temperature: ParseFloat(model.Temperature),
            topK: ParseInt(model.TopK),
            topP: ParseFloat(model.TopP),
            minP: ParseFloat(model.MinP),
            bestOf: null,
            maxTokenType: model.MaxTokenType,
            maxTokens: ParseInt(model.MaxTokens),
            frequencyPenalty: ParseFloat(model.FrequencyPenalty),
            presencePenalty: ParseFloat(model.PresencePenalty),
            repetitionPenalty: ParseFloat(model.RepetitionPenalty),
            stopSequences: null,
            guidanceType: GuidanceType.Json,
            guidanceString: AgentStepSchema.Schema,
            useSearchGrounding: null,
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
