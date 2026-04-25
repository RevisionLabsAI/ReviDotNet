// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Represents a custom tool defined in a .tool rconfig file.
/// Maps to the [[information]], [[general]], and [[mcp]] sections.
///
/// RConfig key format:
///   information_name          → Name
///   information_description   → Description
///   general_type              → Type (mcp, http, builtin)
///   general_enabled           → Enabled
///   mcp_transport             → McpTransport (stdio, http)
///   mcp_server-command        → ServerCommand
///   mcp_server-url            → ServerUrl
///   mcp_capabilities          → Capabilities (comma-separated)
/// </summary>
public class ToolProfile
{
    [RConfigProperty("information_name")]
    public string? Name { get; set; }

    [RConfigProperty("information_description")]
    public string? Description { get; set; }

    [RConfigProperty("general_type")]
    public ToolType Type { get; set; } = ToolType.Mcp;

    [RConfigProperty("general_enabled")]
    public bool Enabled { get; set; } = true;

    [RConfigProperty("mcp_transport")]
    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    /// <summary>Command used to launch the MCP server process (stdio transport).</summary>
    [RConfigProperty("mcp_server-command")]
    public string? ServerCommand { get; set; }

    /// <summary>Base URL for HTTP MCP servers.</summary>
    [RConfigProperty("mcp_server-url")]
    public string? ServerUrl { get; set; }

    /// <summary>MCP tool IDs this profile exposes, parsed from the capabilities key.</summary>
    public List<string> Capabilities { get; set; } = new();

    public void Init()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Tool name must not be null or empty.");
    }
}
