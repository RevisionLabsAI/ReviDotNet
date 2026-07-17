// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// How an OpenAI-protocol provider expresses JSON guidance on the wire. Most OpenAI-compatible hosts
/// accept the strict <c>response_format: json_schema</c> form, but some (e.g. Z.ai's GLM API) only
/// accept <c>json_object</c> and reject the schema form outright. Configured per provider via the
/// <c>[[guidance]] json-schema-mode</c> rcfg key; ignored by non-OpenAI protocols.
/// </summary>
public enum JsonSchemaMode
{
    /// <summary>
    /// Send the full strict schema: <c>response_format = { type: "json_schema", json_schema: … }</c>.
    /// This is the default and what OpenAI, Kimi/Moonshot, Groq, etc. enforce with constrained decoding.
    /// </summary>
    JsonSchema,

    /// <summary>
    /// Send only <c>response_format = { type: "json_object" }</c> and append the schema as an extra
    /// system message so the model still knows the expected shape. Valid-JSON output is enforced on
    /// the wire, but schema conformance relies on the model — callers should validate the result.
    /// </summary>
    JsonObject,
}
