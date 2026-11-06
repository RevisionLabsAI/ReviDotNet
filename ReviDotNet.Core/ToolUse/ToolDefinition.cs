// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json.Linq;

namespace Revi.ToolUse;

/// <summary>
/// A tool the model may call natively (a provider "tool" entry), as opposed to the prompt-described
/// tools of <see cref="AgentRunner"/>. A client tool is one the caller executes; a server tool is one
/// the provider executes itself (for example Anthropic's web search) and is declared by its provider
/// type only.
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>The name the model uses to call the tool. Letters, digits, underscores and hyphens.</summary>
    public string Name { get; }

    /// <summary>What the tool does and when to use it. The model reads this; keep it exact.</summary>
    public string Description { get; }

    /// <summary>The JSON Schema of the tool's input object. Null for a server tool.</summary>
    public JObject? InputSchema { get; }

    /// <summary>
    /// When true the provider guarantees the call's input validates against <see cref="InputSchema"/>
    /// (Anthropic <c>strict</c>). The schema must then have <c>additionalProperties: false</c> and a
    /// <c>required</c> list; <see cref="ClaudePayloads"/> adds both.
    /// </summary>
    public bool Strict { get; }

    /// <summary>
    /// When true the tool is not placed in the prompt until the model finds it through a tool-search
    /// tool (Anthropic <c>defer_loading</c>). Keeps a long tool list out of the cached prefix.
    /// </summary>
    public bool DeferLoading { get; }

    /// <summary>
    /// For a server tool, the provider type string (for example <c>web_search_20260209</c>); null for
    /// a client tool.
    /// </summary>
    public string? ProviderType { get; }

    /// <summary>Extra provider fields for a server tool (for example <c>max_uses</c>).</summary>
    public JObject? ProviderFields { get; }

    /// <summary>
    /// Whether <see cref="ClaudePayloads"/> puts a cache breakpoint on this tool. The caller sets it on
    /// the last tool of a stable list so the whole list is cached.
    /// </summary>
    public bool CacheBreakpoint { get; init; }

    /// <summary>Creates a client tool.</summary>
    public ToolDefinition(string name, string description, JObject inputSchema, bool strict = false, bool deferLoading = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool needs a name.", nameof(name));
        Name = name;
        Description = description ?? string.Empty;
        InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
        Strict = strict;
        DeferLoading = deferLoading;
    }

    private ToolDefinition(string providerType, string name, JObject? providerFields)
    {
        ProviderType = providerType;
        Name = name;
        Description = string.Empty;
        ProviderFields = providerFields;
    }

    /// <summary>A server tool declared by its provider type.</summary>
    /// <param name="providerType">The provider's tool type, for example <c>web_search_20260209</c>.</param>
    /// <param name="name">The provider's required name for that type, for example <c>web_search</c>.</param>
    /// <param name="fields">Extra fields the type accepts.</param>
    public static ToolDefinition Server(string providerType, string name, JObject? fields = null)
        => new(providerType, name, fields);

    /// <summary>Anthropic's hosted web search tool (the dynamic-filtering variant).</summary>
    /// <param name="maxUses">How many searches one request may make.</param>
    public static ToolDefinition ClaudeWebSearch(int maxUses = 5)
        => Server("web_search_20260209", "web_search", new JObject { ["max_uses"] = maxUses });

    /// <summary>Anthropic's hosted web fetch tool (the dynamic-filtering variant).</summary>
    /// <param name="maxUses">How many fetches one request may make.</param>
    public static ToolDefinition ClaudeWebFetch(int maxUses = 5)
        => Server("web_fetch_20260209", "web_fetch", new JObject { ["max_uses"] = maxUses });

    /// <summary>
    /// Anthropic's regex tool-search tool. Mark the tools it should find with <see cref="DeferLoading"/>;
    /// at least one tool in the request must stay non-deferred.
    /// </summary>
    public static ToolDefinition ClaudeToolSearch()
        => Server("tool_search_tool_regex_20251119", "tool_search_tool_regex");

    /// <summary>Whether this is a server tool.</summary>
    public bool IsServerTool => ProviderType is not null;

    /// <summary>Builds a client tool from a schema given as JSON text.</summary>
    public static ToolDefinition FromJsonSchema(string name, string description, string inputSchemaJson, bool strict = false, bool deferLoading = false)
        => new(name, description, JObject.Parse(inputSchemaJson), strict, deferLoading);
}
