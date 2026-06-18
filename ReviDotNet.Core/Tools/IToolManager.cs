// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>DI interface for the tool registry (built-in and custom tools).</summary>
public interface IToolManager
{
    /// <summary>Loads custom tool profiles from the application assembly.</summary>
    Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Additively loads custom tool profiles from <paramref name="rootDirectory"/>/<c>Tools/</c> on disk
    /// (an extra <c>RConfigs</c> root). Existing tools are kept on a name clash; a missing folder is a no-op.
    /// Does not affect code-registered built-in tools.
    /// </summary>
    void LoadDirectory(string rootDirectory);

    /// <summary>Registers a built-in tool. Overwrites any existing tool with the same name.</summary>
    void Register(IBuiltInTool tool);

    /// <summary>Removes a built-in tool by name. Returns true if removed.</summary>
    bool Unregister(string name);

    /// <summary>Returns the built-in tool with the given name, or null.</summary>
    IBuiltInTool? GetBuiltIn(string name);

    /// <summary>Returns all registered built-in tool names.</summary>
    IReadOnlyCollection<string> GetBuiltInNames();

    /// <summary>Returns the custom tool profile with the given name, or null.</summary>
    ToolProfile? GetCustom(string name);

    /// <summary>Returns all loaded custom tool profiles.</summary>
    List<ToolProfile> GetAllCustom();
}
