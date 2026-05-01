// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Provides the JSON schema string injected as the guidance schema for every agent step call.
/// This enforces that the LLM always returns a valid AgentStepResponse JSON object.
/// </summary>
public static class AgentStepSchema
{
    /// <summary>
    /// JSON Schema (draft-07) for AgentStepResponse.
    /// Passed as guidanceString when calling inference with GuidanceType.Json.
    /// </summary>
    public static readonly string Schema = """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "type": "object",
          "required": ["signal", "tool_calls", "content"],
          "properties": {
            "signal": {
              "type": ["string", "null"],
              "description": "Transition signal name (e.g. DONE, CONTINUE, ABORT). Null if no transition."
            },
            "tool_calls": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["name", "input"],
                "properties": {
                  "name":  { "type": "string", "description": "Tool name to invoke." },
                  "input": { "type": "string", "description": "Freeform input for the tool." }
                },
                "additionalProperties": false
              }
            },
            "content": {
              "type": "string",
              "description": "Main reasoning or output text for this step."
            },
            "thinking": {
              "type": ["string", "null"],
              "description": "Optional extended reasoning surfaced separately from content for trace visibility. Null when not used."
            }
          },
          "additionalProperties": false
        }
        """;
}
