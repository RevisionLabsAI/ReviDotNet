// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// Built-in tool that lets one agent invoke another. Sub-agents are first-class:
/// their full ReviLog tree (LLM calls, tool dispatches, state transitions, end)
/// nests under the parent agent's tool-call event automatically.
///
/// Input format (JSON):
///   { "agent": "research/research-agent", "task": "find papers on X", "inputs": { "depth": 2 } }
///
/// Either "task" or "inputs" may be omitted. If "task" is provided and "inputs" isn't,
/// the task is forwarded as inputs["input"] (matching Agent.Run(name, input) semantics).
/// </summary>
public class InvokeAgentTool : IBuiltInTool
{
    public string Name => "invoke_agent";

    public string Description =>
        "Invokes another registered agent as a sub-agent. " +
        "Input is JSON: { \"agent\": \"<agent-name>\", \"task\": \"<text>\", \"inputs\": { ... } }. " +
        "Returns the sub-agent's final output. Subject to MaxAgentDepth guardrail.";

    public async Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        InvocationRequest? req;
        try
        {
            req = ParseInput(input);
        }
        catch (Exception ex)
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = $"Could not parse invoke_agent input as JSON: {ex.Message}. Expected: {{\"agent\":\"<name>\",\"task\":\"<text>\"}}"
            };
        }

        if (req == null || string.IsNullOrWhiteSpace(req.Agent))
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = "invoke_agent requires an \"agent\" field naming the sub-agent to run."
            };
        }

        // Recover the parent context that AgentRunner pushed before this tool dispatched.
        AgentRunContext? ambient = AgentRunContext.Current;
        if (ambient == null || ambient.ParentLog == null)
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = "invoke_agent must be dispatched from within an AgentRunner — no ambient run context."
            };
        }

        // Enforce the MaxAgentDepth guardrail. We don't have direct access to the parent
        // state's guardrails here, so we apply the runner-wide default. State-level overrides
        // are validated at AgentRunner construction time (the sub-agent will refuse if its
        // own state guardrails forbid it).
        int nextDepth = ambient.Depth + 1;
        if (nextDepth > AgentRunner.DefaultMaxAgentDepth)
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = $"invoke_agent refused: would exceed MaxAgentDepth ({AgentRunner.DefaultMaxAgentDepth})."
            };
        }

        Dictionary<string, object> inputs = BuildInputs(req);
        AgentRunContext childCtx = ambient.Child(ambient.ParentLog);

        try
        {
            AgentResult result = await Agent.Run(req.Agent, inputs, childCtx, token);

            return new ToolCallResult
            {
                ToolName = Name,
                Output = result.FinalOutput ?? "",
                Failed = result.ExitReason != AgentExitReason.Completed,
                ErrorMessage = result.ExitReason == AgentExitReason.Completed
                    ? null
                    : $"Sub-agent '{req.Agent}' exited with {result.ExitReason}" +
                      (string.IsNullOrEmpty(result.GuardrailViolationMessage) ? "" : $": {result.GuardrailViolationMessage}")
            };
        }
        catch (Exception ex)
        {
            return new ToolCallResult
            {
                ToolName = Name,
                Failed = true,
                ErrorMessage = $"Sub-agent '{req.Agent}' threw: {ex.Message}"
            };
        }
    }

    private static InvocationRequest? ParseInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        return JsonConvert.DeserializeObject<InvocationRequest>(input);
    }

    private static Dictionary<string, object> BuildInputs(InvocationRequest req)
    {
        var dict = new Dictionary<string, object>();

        if (req.Inputs is JObject jo)
        {
            foreach (var prop in jo.Properties())
            {
                object? value = prop.Value.Type switch
                {
                    JTokenType.String => prop.Value.Value<string>(),
                    JTokenType.Integer => prop.Value.Value<long>(),
                    JTokenType.Float => prop.Value.Value<double>(),
                    JTokenType.Boolean => prop.Value.Value<bool>(),
                    _ => prop.Value.ToString()
                };
                if (value != null)
                    dict[prop.Name] = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(req.Task) && !dict.ContainsKey("input") && !dict.ContainsKey("task"))
            dict["input"] = req.Task;

        return dict;
    }

    private sealed class InvocationRequest
    {
        [JsonProperty("agent")] public string Agent { get; set; } = "";
        [JsonProperty("task")] public string? Task { get; set; }
        [JsonProperty("inputs")] public JObject? Inputs { get; set; }
    }
}
